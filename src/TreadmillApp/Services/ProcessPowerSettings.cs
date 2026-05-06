using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TreadmillApp.Services;

/// <summary>
/// Opts this process out of Windows EcoQoS / power-throttling so BLE
/// notifications keep flowing at full speed when the main window is
/// hidden to the system tray. Without this, Windows reduces execution
/// speed and timer resolution for "background" processes (no visible
/// window), which causes the WinRT BLE stack to drop or delay vendor
/// notifications mid-walk.
/// </summary>
internal static class ProcessPowerSettings
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private const int  ProcessPowerThrottling                       = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION     = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED     = 0x1;
    private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int    ProcessInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE ProcessInformation,
        uint   ProcessInformationSize);

    public static void DisableEcoQos()
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version     = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                            | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                StateMask   = 0, // 0 in StateMask = OPT OUT of throttling
            };

            SetProcessInformation(
                Process.GetCurrentProcess().Handle,
                ProcessPowerThrottling,
                ref state,
                (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch
        {
            // Best-effort; older Windows versions may not support this.
        }
    }
}
