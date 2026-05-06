using System.Collections.Generic;

namespace TreadmillApp.Services
{
    /// <summary>
    /// Maps standard GATT appearance values to human-readable device type strings.
    /// Appearance values are defined in Bluetooth SIG Assigned Numbers document.
    /// </summary>
    public static class GattAppearanceMapper
    {
        private static readonly Dictionary<ushort, string> AppearanceMap = new()
        {
            // Fitness Equipment (Indoor Sports Activity category)
            { 0x0521, "Treadmill" },
            { 0x0522, "Cross Trainer" },
            { 0x0523, "Step Climbing" },
            { 0x0524, "Rowing Machine" },
            { 0x0525, "Indoor Bike" },
            { 0x0526, "Outdoor Bike" },
            { 0x0520, "Indoor Sports Activity" },
            
            // Outdoor Sports Activity
            { 0x0500, "Outdoor Sports Activity" },
            
            // Running and Walking
            { 0x0340, "Running Walking Sensor" },
            
            // Heart Rate
            { 0x0310, "Heart Rate Sensor" },
            { 0x0311, "Heart Rate Belt" },
            
            // Cycling
            { 0x0341, "Cycling" },
            
            // Generic HID devices
            { 0x03C0, "Generic HID" },
            { 0x03C1, "Keyboard" },
            { 0x03C2, "Mouse" },
            { 0x03C3, "Joystick" },
            
            // Barcode Scanner
            { 0x0420, "Generic Barcode Scanner" },
        };

        /// <summary>
        /// Maps appearance value to device type string.
        /// Returns "Unknown Device Type (0xXXXX)" for unrecognized values.
        /// </summary>
        /// <param name="appearanceValue">16-bit appearance value from BLE advertisement</param>
        /// <returns>Device type string (e.g., "Treadmill", "Heart Rate Sensor", "Unknown Device Type (0x1234)")</returns>
        public static string GetDeviceType(ushort appearanceValue)
        {
            if (AppearanceMap.TryGetValue(appearanceValue, out var deviceType))
            {
                return deviceType;
            }
            
            // For unrecognized values, show hex with indication it's unknown
            // Per clarification: Show "Unknown Device Type (0xXXXX)" format
            return $"Unknown Device Type (0x{appearanceValue:X4})";
        }
    }
}




