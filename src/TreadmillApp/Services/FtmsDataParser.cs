using System;
using TreadmillApp.Models;

namespace TreadmillApp.Services
{
    /// <summary>
    /// Specialized parser for FTMS (Fitness Machine Service) characteristics.
    /// Parses data according to Bluetooth SIG FTMS Profile specification.
    /// </summary>
    public static class FtmsDataParser
    {
        // FTMS Characteristic UUIDs (16-bit short UUIDs in standard base)
        private static readonly Guid FitnessMachineFeatureUuid = new Guid("00002ACC-0000-1000-8000-00805f9b34fb");
        private static readonly Guid TreadmillDataUuid = new Guid("00002ACD-0000-1000-8000-00805f9b34fb");
        private static readonly Guid SupportedSpeedRangeUuid = new Guid("00002AD4-0000-1000-8000-00805f9b34fb");
        private static readonly Guid SupportedInclineRangeUuid = new Guid("00002AD6-0000-1000-8000-00805f9b34fb");
        private static readonly Guid SupportedResistanceLevelRangeUuid = new Guid("00002AD8-0000-1000-8000-00805f9b34fb");
        private static readonly Guid SupportedPowerRangeUuid = new Guid("00002AD7-0000-1000-8000-00805f9b34fb");

        /// <summary>
        /// Parse Fitness Machine Feature characteristic (0x2ACC).
        /// 8 bytes of bit flags indicating supported features.
        /// </summary>
        /// <param name="data">Byte array with 8 bytes of feature flags</param>
        /// <returns>FitnessMachineFeatures structure, or empty structure if parsing fails</returns>
        public static FitnessMachineFeatures ParseFeatures(byte[] data)
        {
            if (data == null || data.Length < 8)
            {
                return new FitnessMachineFeatures();
            }

            try
            {
                // FTMS Feature flags are in the first 8 bytes (64 bits)
                // Bit 0: Average Speed supported
                // Bit 1: Cadence supported
                // Bit 2: Total Distance supported
                // Bit 3: Inclination/Declination supported
                // Bit 4: Elevation Gain supported
                // Bit 5: Pace supported
                // Bit 6: Step Count supported
                // Bit 7: Resistance Level supported
                // Bit 8: Stride Count supported
                // Bit 9: Expended Energy supported
                // Bit 10: Heart Rate Measurement supported
                // Bit 11: Elapsed Time supported
                // Bit 12: Remaining Time supported
                // Bit 13: Power supported
                // Bit 14: Force on Belt/Wheel supported
                // Bit 15: User Data Retention supported
                // And more...

                return new FitnessMachineFeatures
                {
                    SupportsDistance = (data[0] & 0x04) != 0,      // Bit 2
                    SupportsStepCount = (data[0] & 0x40) != 0,      // Bit 6
                    SupportsIncline = (data[0] & 0x08) != 0,       // Bit 3
                    SupportsElevation = (data[0] & 0x10) != 0,     // Bit 4
                    SupportsResistance = (data[0] & 0x80) != 0,    // Bit 7
                    SupportsPower = (data[1] & 0x02) != 0,         // Bit 13
                    SupportsHeartRate = (data[1] & 0x04) != 0,     // Bit 10
                    SupportsSpeed = (data[0] & 0x01) != 0           // Bit 0
                };
            }
            catch
            {
                return new FitnessMachineFeatures();
            }
        }

        /// <summary>
        /// Parse Supported Range characteristic.
        /// Format: 6 bytes - minimum (2 bytes, little-endian), maximum (2 bytes, little-endian), increment (2 bytes, little-endian).
        /// </summary>
        /// <param name="characteristicUuid">UUID of the range characteristic</param>
        /// <param name="data">Byte array with 6 bytes</param>
        /// <returns>SupportedRange structure with appropriate unit and type, or null if parsing fails</returns>
        public static SupportedRange? ParseSupportedRange(Guid characteristicUuid, byte[] data)
        {
            if (data == null || data.Length < 6)
            {
                return null;
            }

            try
            {
                // Parse little-endian values
                ushort minRaw = (ushort)(data[0] | (data[1] << 8));
                ushort maxRaw = (ushort)(data[2] | (data[3] << 8));
                ushort incrementRaw = (ushort)(data[4] | (data[5] << 8));

                double minimum = minRaw / 100.0;  // FTMS uses 1/100th resolution
                double maximum = maxRaw / 100.0;
                double increment = incrementRaw / 100.0;

                string unit;
                string rangeType;

                // Determine unit and type based on characteristic UUID
                if (characteristicUuid == SupportedSpeedRangeUuid)
                {
                    unit = "km/h";
                    rangeType = "Speed";
                }
                else if (characteristicUuid == SupportedInclineRangeUuid)
                {
                    unit = "degrees";
                    rangeType = "Incline";
                }
                else if (characteristicUuid == SupportedResistanceLevelRangeUuid)
                {
                    unit = "level";
                    rangeType = "Resistance";
                }
                else if (characteristicUuid == SupportedPowerRangeUuid)
                {
                    unit = "watts";
                    rangeType = "Power";
                }
                else
                {
                    unit = "units";
                    rangeType = "Unknown";
                }

                return new SupportedRange
                {
                    Minimum = minimum,
                    Maximum = maximum,
                    Increment = increment,
                    Unit = unit,
                    RangeType = rangeType
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Parse Treadmill Data characteristic (0x2ACD) notification data.
        /// Format: flags (2 bytes) + conditional fields per FTMS v1.0 spec.
        ///
        /// FTMS flag bit meanings (Bluetooth SIG FTMS 1.0, Section 3.70.1):
        ///   Bit 0  – More Data:         0 = Instantaneous Speed IS present
        ///                               1 = Instantaneous Speed is NOT present
        ///   Bit 1  – Average Speed present
        ///   Bit 2  – Total Distance present
        ///   Bit 3  – Inclination + Ramp Angle present  (4 bytes: incline 2 + ramp 2)
        ///   Bit 4  – Elevation Gain present             (4 bytes: pos 2 + neg 2)
        ///   Bit 5  – Instantaneous Pace present
        ///   Bit 6  – Average Pace present
        ///   Bit 7  – Expended Energy present            (5 bytes: total 2 + /hr 2 + /min 1)
        ///   Bit 8  – Heart Rate present
        ///   Bit 9  – Metabolic Equivalent present       (1 byte, unit 0.1)
        ///   Bit 10 – Elapsed Time present
        ///   Bit 11 – Remaining Time present
        ///   Bit 12 – Force on Belt + Power Output present (4 bytes: force 2 + power 2)
        /// </summary>
        public static TreadmillMetrics ParseTreadmillData(byte[] data, ushort flags)
        {
            if (data == null || data.Length < 2)
                return new TreadmillMetrics { Timestamp = DateTime.Now };

            try
            {
                var metrics = new TreadmillMetrics { Timestamp = DateTime.Now };
                int offset = 2;

                // Bit 0: More Data – when 0 speed IS present, when 1 speed is absent.
                bool speedAbsent              = (flags & 0x0001) != 0;
                bool averageSpeedPresent      = (flags & 0x0002) != 0;
                bool totalDistancePresent     = (flags & 0x0004) != 0;
                bool inclinationPresent       = (flags & 0x0008) != 0;
                bool elevationGainPresent     = (flags & 0x0010) != 0;
                bool instantPacePresent       = (flags & 0x0020) != 0;
                bool averagePacePresent       = (flags & 0x0040) != 0;
                bool expendedEnergyPresent    = (flags & 0x0080) != 0;
                bool heartRatePresent         = (flags & 0x0100) != 0;
                bool metabolicEquivPresent    = (flags & 0x0200) != 0;
                bool elapsedTimePresent       = (flags & 0x0400) != 0;
                bool remainingTimePresent     = (flags & 0x0800) != 0;
                bool forcePowerPresent        = (flags & 0x1000) != 0;

                // Instantaneous Speed (km/h, resolution 0.01)
                if (!speedAbsent && offset + 2 <= data.Length)
                {
                    ushort raw = (ushort)(data[offset] | (data[offset + 1] << 8));
                    metrics.CurrentSpeed = raw / 100.0;
                    offset += 2;
                }

                // Average Speed (km/h, resolution 0.01)
                if (averageSpeedPresent && offset + 2 <= data.Length)
                {
                    ushort raw = (ushort)(data[offset] | (data[offset + 1] << 8));
                    metrics.AverageSpeed = raw / 100.0;
                    offset += 2;
                }

                // Total Distance (meters, 3 bytes unsigned)
                if (totalDistancePresent && offset + 3 <= data.Length)
                {
                    metrics.TotalDistance = (uint)(data[offset]
                                                 | (data[offset + 1] << 8)
                                                 | (data[offset + 2] << 16));
                    offset += 3;
                }

                // Inclination (signed, 0.1°) + Ramp Angle Setting (signed, 0.1°) = 4 bytes
                if (inclinationPresent && offset + 4 <= data.Length)
                {
                    short inclRaw = (short)(data[offset] | (data[offset + 1] << 8));
                    metrics.Inclination = inclRaw / 10.0;
                    offset += 4; // inclination (2) + ramp angle (2)
                }

                // Positive Elevation Gain (m, 0.1) + Negative Elevation Gain (m, 0.1) = 4 bytes
                if (elevationGainPresent && offset + 4 <= data.Length)
                {
                    ushort elevRaw = (ushort)(data[offset] | (data[offset + 1] << 8));
                    metrics.ElevationGain = elevRaw;
                    offset += 4; // positive (2) + negative (2)
                }

                // Instantaneous Pace (seconds/km, resolution 0.1)
                if (instantPacePresent && offset + 2 <= data.Length)
                {
                    ushort raw = (ushort)(data[offset] | (data[offset + 1] << 8));
                    metrics.Pace = raw / 10.0;
                    offset += 2;
                }

                // Average Pace (seconds/km, resolution 0.1)
                if (averagePacePresent && offset + 2 <= data.Length)
                    offset += 2;

                // Expended Energy: Total (kcal, 2 bytes) + Per Hour (kcal, 2 bytes) + Per Minute (kcal, 1 byte)
                if (expendedEnergyPresent && offset + 5 <= data.Length)
                {
                    ushort energy = (ushort)(data[offset] | (data[offset + 1] << 8));
                    metrics.ExpendedEnergy = energy;
                    offset += 5;
                }

                // Heart Rate (bpm, 1 byte)
                if (heartRatePresent && offset + 1 <= data.Length)
                {
                    metrics.HeartRate = data[offset];
                    offset += 1;
                }

                // Metabolic Equivalent (unit 0.1, 1 byte)
                if (metabolicEquivPresent && offset + 1 <= data.Length)
                    offset += 1;

                // Elapsed Time (seconds, 2 bytes)
                if (elapsedTimePresent && offset + 2 <= data.Length)
                {
                    metrics.ElapsedSeconds = (ushort)(data[offset] | (data[offset + 1] << 8));
                    offset += 2;
                }

                // Remaining Time (seconds, 2 bytes)
                if (remainingTimePresent && offset + 2 <= data.Length)
                    offset += 2;

                // Force on Belt (N) + Power Output (W) = 4 bytes
                if (forcePowerPresent && offset + 4 <= data.Length)
                    offset += 4;

                return metrics;
            }
            catch
            {
                return new TreadmillMetrics { Timestamp = DateTime.Now };
            }
        }
    }
}

