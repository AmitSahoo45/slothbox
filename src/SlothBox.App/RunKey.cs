using Microsoft.Win32;
using SlothBox.Core;

namespace SlothBox.App;

// Spec 4.3 "Auto-start at logon": HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SlothBox = "<path to exe>", with
// no admin rights. On means the value starts this exe.
internal sealed class RunKey : IAutoStart
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "SlothBox";

    private static string Command => $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\"";

    public bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(Run);
            return run?.GetValue(Name) is string command &&
                   string.Equals(command, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(Run, writable: true);
        if (enabled)
            run.SetValue(Name, Command, RegistryValueKind.String);
        else
            run.DeleteValue(Name, throwOnMissingValue: false);
    }
}
