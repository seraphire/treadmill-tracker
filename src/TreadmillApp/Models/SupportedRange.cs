namespace TreadmillApp.Models
{
    /// <summary>
    /// Represents a parsed supported range characteristic (speed, incline, resistance, power).
    /// Derived from Supported Range characteristics (0x2AD4, 0x2AD6, 0x2AD8, 0x2AD7).
    /// </summary>
    public class SupportedRange
    {
        /// <summary>
        /// Minimum value.
        /// </summary>
        public double Minimum { get; set; }

        /// <summary>
        /// Maximum value.
        /// </summary>
        public double Maximum { get; set; }

        /// <summary>
        /// Step increment.
        /// </summary>
        public double Increment { get; set; }

        /// <summary>
        /// Unit of measurement (e.g., "km/h", "degrees", "watts").
        /// </summary>
        public string Unit { get; set; } = string.Empty;

        /// <summary>
        /// Type of range (e.g., "Speed", "Incline", "Resistance", "Power").
        /// </summary>
        public string RangeType { get; set; } = string.Empty;

        /// <summary>
        /// Display-friendly representation.
        /// </summary>
        public override string ToString()
        {
            return $"{Minimum:F1} - {Maximum:F1} {Unit} (increment: {Increment:F1})";
        }
    }
}

