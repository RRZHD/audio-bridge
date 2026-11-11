using System;
using System.Diagnostics;

namespace AudioBridge.Server.Core;

/// <summary>
/// Windows Firewall is ON only for the "Public" profile on many machines — and a phone's USB
/// tethering adapter is classified as Public. If the first "allow access?" prompt was dismissed (or
/// only "Private" was ticked), Windows keeps a Block rule for the exe and the phone can neither
/// discover nor connect to the server over USB. This detects that and, on the user's explicit
/// consent (UAC), replaces the rules for this exe with a single "allow inbound, all profiles".
/// </summary>
public static class FirewallHelper
{
    private const int ProfilePublic = 4;     // NET_FW_PROFILE2_PUBLIC
    private const int DirectionIn = 1;       // NET_FW_RULE_DIR_IN
    private const int ActionAllow = 1;       // NET_FW_ACTION_ALLOW
    private const string RuleName = "Audio Bridge Server";

    public static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>
    /// False only when we can positively tell inbound connections on Public networks are blocked for
    /// this exe (no enabled Allow rule covering Public, or an enabled Block rule — Block wins).
    /// If anything about the check fails we return true: never nag on uncertainty.
    /// </summary>
    public static bool IsPublicInboundAllowed(string? exeOverride = null)
    {
        try
        {
            var exe = exeOverride ?? ExePath;
            if (exe.Length == 0) return true;

            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null) return true;
            dynamic policy = Activator.CreateInstance(type)!;

            // If the Public profile's firewall is off, nothing is blocked.
            if (!(bool)policy.FirewallEnabled[ProfilePublic]) return true;

            var allowed = false;
            var blocked = false;
            foreach (dynamic rule in policy.Rules)
            {
                if (!(bool)rule.Enabled || (int)rule.Direction != DirectionIn) continue;
                var app = rule.ApplicationName as string;
                if (string.IsNullOrEmpty(app) || !string.Equals(app, exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (((int)rule.Profiles & ProfilePublic) == 0) continue;

                if ((int)rule.Action == ActionAllow) allowed = true;
                else blocked = true;
            }
            return allowed && !blocked;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Shows the UAC prompt and, if accepted, (re)creates the allow rule. Returns false if declined/failed.</summary>
    public static bool AllowThroughFirewallElevated()
    {
        var exe = ExePath;
        if (exe.Length == 0) return false;

        // Drop every existing rule for this exe (including the Block ones Windows auto-created),
        // then add one allow rule covering all profiles.
        var commands =
            $"netsh advfirewall firewall delete rule name=all program=\"{exe}\" & " +
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {commands}")
            {
                Verb = "runas",          // UAC consent — the user decides
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            process?.WaitForExit(15000);
            return process is { ExitCode: 0 };
        }
        catch
        {
            return false; // UAC declined, or cmd couldn't start
        }
    }
}
