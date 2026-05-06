using System;

namespace TreadmillApp.Models
{
    /// <summary>
    /// Represents live treadmill data from Treadmill Data characteristic (0x2ACD) notifications.
    /// </summary>
    public class TreadmillMetrics
    {
        /// <summary>
        /// Current speed in km/h or mph, nullable.
        /// </summary>
        public double? CurrentSpeed { get; set; }

        /// <summary>
        /// Average speed, nullable.
        /// </summary>
        public double? AverageSpeed { get; set; }

        /// <summary>
        /// Total distance in meters, nullable.
        /// </summary>
        public uint? TotalDistance { get; set; }

        /// <summary>
        /// Current inclination in degrees, nullable.
        /// </summary>
        public double? Inclination { get; set; }

        /// <summary>
        /// Cumulative elevation gain in meters, nullable.
        /// </summary>
        public uint? ElevationGain { get; set; }

        /// <summary>
        /// Current pace, nullable.
        /// </summary>
        public double? Pace { get; set; }

        /// <summary>
        /// Step count, nullable.
        /// </summary>
        public ushort? StepCount { get; set; }

        /// <summary>
        /// Current resistance level, nullable.
        /// </summary>
        public ushort? ResistanceLevel { get; set; }

        /// <summary>
        /// Energy expended in kilocalories, nullable.
        /// </summary>
        public uint? ExpendedEnergy { get; set; }

        /// <summary>
        /// Heart rate in bpm, nullable.
        /// </summary>
        public byte? HeartRate { get; set; }

        /// <summary>
        /// Elapsed workout time in seconds, nullable.
        /// </summary>
        public ushort? ElapsedSeconds { get; set; }

        /// <summary>
        /// When the data was received.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Display-friendly representation showing available metrics.
        /// </summary>
        public override string ToString()
        {
            var metrics = new List<string>();

            if (CurrentSpeed.HasValue)
                metrics.Add($"Speed: {CurrentSpeed.Value:F1} km/h");

            if (TotalDistance.HasValue)
                metrics.Add($"Distance: {TotalDistance.Value} m");

            if (StepCount.HasValue)
                metrics.Add($"Steps: {StepCount.Value}");

            if (Inclination.HasValue)
                metrics.Add($"Incline: {Inclination.Value:F1}°");

            if (HeartRate.HasValue)
                metrics.Add($"HR: {HeartRate.Value} bpm");

            return metrics.Count > 0 
                ? string.Join(" | ", metrics)
                : "No metrics available";
        }
    }
}

