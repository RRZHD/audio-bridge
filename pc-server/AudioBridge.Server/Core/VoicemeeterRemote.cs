using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace AudioBridge.Server.Core;

/// <summary>
/// Voicemeeter (free virtual mixer from VB-Audio) is used as the second virtual cable for the phone's
/// microphone: what the server plays into "Voicemeeter Input" is routed to bus B1, which programs see as the
/// microphone "Voicemeeter Out B1". This talks to its Remote API only to set that routing up (and to start
/// the mixer if it is installed but not running) — all failures are non-fatal.
/// </summary>
public static class VoicemeeterRemote
{
    private const string InputName = "Voicemeeter Input";
    private const string MicName = "Voicemeeter Out B1";

    /// <summary>True for the one render endpoint of Voicemeeter the server uses as a cable.</summary>
    public static bool IsMainInput(string friendlyName) => friendlyName.StartsWith(InputName + " (", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the one capture endpoint (bus B1) that carries the phone's voice.</summary>
    public static bool IsMainMic(string friendlyName) => friendlyName.StartsWith(MicName + " (", StringComparison.OrdinalIgnoreCase);

    /// <summary>The other 15 or so Voicemeeter endpoints (In 1..5, AUX, VAIO3, Out A1..A5, B2, B3): noise in the pickers.</summary>
    public static bool IsClutter(string friendlyName) =>
        friendlyName.StartsWith("Voicemeeter", StringComparison.OrdinalIgnoreCase) && !IsMainInput(friendlyName) && !IsMainMic(friendlyName);

    private static string? DllPath()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1236-5467}");
            var uninstall = key?.GetValue("UninstallString") as string;
            if (!string.IsNullOrEmpty(uninstall))
            {
                var dir = Path.GetDirectoryName(uninstall.Trim('"').Split(" -")[0].Trim('"'));
                if (dir != null && File.Exists(Path.Combine(dir, "VoicemeeterRemote64.dll"))) return Path.Combine(dir, "VoicemeeterRemote64.dll");
            }
        }
        catch { }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VB", "Voicemeeter", "VoicemeeterRemote64.dll");
        return File.Exists(fallback) ? fallback : null;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Login();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Logout();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RunVoicemeeter(int type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetVmType(out int type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)] private delegate int SetParameters(string script);

    /// <summary>
    /// Routes the virtual input strip to B1 only (not to the speakers). Starts Voicemeeter if needed.
    /// Returns a short message for the log, or null if Voicemeeter isn't installed.
    /// </summary>
    public static string? ConfigureMicRoute()
    {
        var path = DllPath();
        if (path == null) return null;
        var lib = IntPtr.Zero;
        var loggedIn = false;
        Logout? logout = null;
        try
        {
            lib = NativeLibrary.Load(path);
            var login = Marshal.GetDelegateForFunctionPointer<Login>(NativeLibrary.GetExport(lib, "VBVMR_Login"));
            logout = Marshal.GetDelegateForFunctionPointer<Logout>(NativeLibrary.GetExport(lib, "VBVMR_Logout"));
            var run = Marshal.GetDelegateForFunctionPointer<RunVoicemeeter>(NativeLibrary.GetExport(lib, "VBVMR_RunVoicemeeter"));
            var getType = Marshal.GetDelegateForFunctionPointer<GetVmType>(NativeLibrary.GetExport(lib, "VBVMR_GetVoicemeeterType"));
            var set = Marshal.GetDelegateForFunctionPointer<SetParameters>(NativeLibrary.GetExport(lib, "VBVMR_SetParameters"));

            var wasRunning = Process.GetProcessesByName("voicemeeter_x64").Length + Process.GetProcessesByName("voicemeeterpro_x64").Length
                             + Process.GetProcessesByName("voicemeeter8x64").Length > 0;
            var rc = login();
            if (rc < 0) return "Voicemeeter: не удалось подключиться к программе.";
            loggedIn = true;
            if (rc == 1) { run(1); wasRunning = false; } // not running: start the Standard edition

            var type = 0;
            for (var i = 0; i < 40 && type == 0; i++) // up to ~10 s while it starts
            {
                getType(out type);
                if (type == 0) Thread.Sleep(250);
            }
            if (type == 0) return "Voicemeeter: программа не ответила.";

            // Hardware inputs come first; the virtual input ("Voicemeeter VAIO") is the next strip.
            var strip = type switch { 1 => 2, 2 => 3, _ => 5 };
            var buses = type switch { 1 => 1, 2 => 3, _ => 5 };
            var script = "";
            for (var a = 1; a <= buses; a++) script += $"Strip[{strip}].A{a}=0;";
            script += $"Strip[{strip}].B1=1;";
            if (type >= 2) script += $"Strip[{strip}].B2=0;";
            if (type >= 3) script += $"Strip[{strip}].B3=0;";
            set(script);
            Thread.Sleep(600); // a Logout right after the call can drop it; let Voicemeeter apply the script first
            return wasRunning ? "Voicemeeter: маршрут микрофона настроен (вход → B1)."
                              : "Voicemeeter запущен, маршрут микрофона настроен (вход → B1).";
        }
        catch (Exception ex)
        {
            return $"Voicemeeter: не удалось настроить — {ex.Message}";
        }
        finally
        {
            try { if (loggedIn) logout?.Invoke(); } catch { }
            if (lib != IntPtr.Zero) { try { NativeLibrary.Free(lib); } catch { } }
        }
    }
}
