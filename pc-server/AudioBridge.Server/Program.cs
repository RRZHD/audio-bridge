using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AudioBridge.Server;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ProcessTuning.Apply();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(autoStart: args.Contains("--autostart", StringComparer.OrdinalIgnoreCase)));
    }
}

/// <summary>
/// Real-time audio must not be throttled while the window is minimized or in the background.
/// Windows 11 puts such processes into "efficiency mode" and coarsens timer resolution, which shows
/// up as irregular delivery; opt out of both, and ask for 1 ms timer resolution.
/// </summary>
internal static class ProcessTuning
{
    private const int ProcessPowerThrottling = 4;
    private const uint ThrottlingCurrentVersion = 1;
    private const uint ThrottlingExecutionSpeed = 0x1;
    private const uint ThrottlingIgnoreTimerResolution = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

    public static void Apply()
    {
        try
        {
            timeBeginPeriod(1);
            // ControlMask says "I am taking control of these", StateMask = 0 says "throttling OFF".
            var state = new PowerThrottlingState
            {
                Version = ThrottlingCurrentVersion,
                ControlMask = ThrottlingExecutionSpeed | ThrottlingIgnoreTimerResolution,
                StateMask = 0,
            };
            SetProcessInformation(Process.GetCurrentProcess().Handle, ProcessPowerThrottling, ref state,
                Marshal.SizeOf<PowerThrottlingState>());
        }
        catch
        {
            // Best effort — older Windows versions simply don't have these knobs.
        }
    }
}
