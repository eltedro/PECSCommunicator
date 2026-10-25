using Microsoft.Win32;

namespace PECSCommunicator;

/// <summary>
/// TODO #8: "Start with Windows" for the current user, via
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value "PECSCommunicator" = "quoted exe path".
/// Per-user, so no admin rights are needed. Off unless turned on in Settings.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PECSCommunicator";

    /// <summary>Quoted path of this exe (quotes keep paths with spaces working).</summary>
    private static string Command => $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\"";

    /// <summary>True if the Run value exists (whatever path it points to).</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>Adds (pointing at this exe) or removes the Run value. Throws on registry errors.</summary>
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
