namespace TreadmillApp.Models
{
    /// <summary>
    /// Represents parsed FTMS Fitness Machine Feature characteristic (0x2ACC).
    /// Contains bit flags indicating supported treadmill capabilities.
    /// </summary>
    public class FitnessMachineFeatures
    {
        /// <summary>
        /// Distance tracking supported.
        /// </summary>
        public bool SupportsDistance { get; set; }

        /// <summary>
        /// Step count tracking supported.
        /// </summary>
        public bool SupportsStepCount { get; set; }

        /// <summary>
        /// Incline adjustment supported.
        /// </summary>
        public bool SupportsIncline { get; set; }

        /// <summary>
        /// Elevation gain tracking supported.
        /// </summary>
        public bool SupportsElevation { get; set; }

        /// <summary>
        /// Resistance level supported.
        /// </summary>
        public bool SupportsResistance { get; set; }

        /// <summary>
        /// Power measurement supported.
        /// </summary>
        public bool SupportsPower { get; set; }

        /// <summary>
        /// Heart rate monitoring supported.
        /// </summary>
        public bool SupportsHeartRate { get; set; }

        /// <summary>
        /// Speed tracking supported.
        /// </summary>
        public bool SupportsSpeed { get; set; }

        /// <summary>
        /// Display-friendly representation showing supported features.
        /// </summary>
        public override string ToString()
        {
            var features = new List<string>();
            if (SupportsSpeed) features.Add("Speed");
            if (SupportsDistance) features.Add("Distance");
            if (SupportsStepCount) features.Add("Steps");
            if (SupportsIncline) features.Add("Incline");
            if (SupportsElevation) features.Add("Elevation");
            if (SupportsResistance) features.Add("Resistance");
            if (SupportsPower) features.Add("Power");
            if (SupportsHeartRate) features.Add("Heart Rate");

            return features.Count > 0 
                ? string.Join(" | ", features) + " supported"
                : "No features detected";
        }
    }
}

