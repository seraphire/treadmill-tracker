using System;

namespace TreadmillApp.Models
{
    /// <summary>
    /// Represents a discovered or connected Bluetooth Low Energy device.
    /// </summary>
    public class BleDevice
    {
        /// <summary>
        /// Unique identifier from Windows.Devices.Bluetooth (e.g., device address).
        /// Required, cannot be null/empty.
        /// </summary>
        public string DeviceId { get; set; } = string.Empty;

        /// <summary>
        /// Raw Bluetooth address (ulong) for connection purposes.
        /// Used with FromBluetoothAddressAsync instead of FromIdAsync.
        /// </summary>
        public ulong BluetoothAddress { get; set; }

        /// <summary>
        /// Device name/advertisement name. May be empty if not advertised.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// MAC address in canonical format, e.g., "AA:BB:CC:DD:EE:FF".
        /// Must be valid MAC address (colon-separated hex pairs).
        /// </summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// RSSI value in dBm (signal strength indicator). Nullable.
        /// </summary>
        public int? SignalStrength { get; set; }

        /// <summary>
        /// Current connection state. Must accurately reflect actual connection state.
        /// </summary>
        public bool IsConnected { get; set; }

        /// <summary>
        /// When connection was established. Nullable if not connected.
        /// </summary>
        public DateTime? ConnectionTimestamp { get; set; }

        /// <summary>
        /// Device type derived from appearance value in advertisement data.
        /// Shows "Unknown" when appearance data is unavailable.
        /// May show "Unknown Device Type (0xXXXX)" for unrecognized appearance values.
        /// </summary>
        public string? DeviceType { get; set; }

        /// <summary>
        /// Display-friendly representation of the device.
        /// </summary>
        public override string ToString()
        {
            return $"{Name} ({Address})";
        }
    }
}

