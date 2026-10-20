using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ShellFlow.App.Services;

/// <summary>
/// Makes double-clicking .bat / .cmd files run them in ShellFlow, per user and reversibly. It adds a "Run in ShellFlow"
/// verb to the batfile / cmdfile classes under HKCU and, when enabled, makes it the default verb. Windows' own "open"
/// verb is never modified, so turning the option off (or uninstalling) restores the normal console behavior.
/// </summary>
public static partial class ScriptAssociation
{
    public const string VerbName = "ShellFlow";
    private static readonly string[] ProgIds = ["batfile", "cmdfile"];

    public static bool IsEnabled()
    {
        foreach (var progId in ProgIds)
        {
            using var shell = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{progId}\shell");
            if (!string.Equals(shell?.GetValue(null) as string, VerbName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Adds the "Run in ShellFlow" verb (the installer adds it too) pointing at this executable.</summary>
    public static void EnsureVerb(string executable)
    {
        foreach (var progId in ProgIds)
        {
            using var verb = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\shell\{VerbName}");
            verb.SetValue(null, "Run in ShellFlow");
            verb.SetValue("Icon", $"\"{executable}\",0");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{executable}\" --run \"%1\"");
        }
    }

    public static void Enable(string executable)
    {
        EnsureVerb(executable);
        foreach (var progId in ProgIds)
        {
            using var shell = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\shell");
            shell.SetValue(null, VerbName);
        }

        NotifyShell();
        Log.Info("Double-clicking .bat/.cmd files now runs them in ShellFlow.");
    }

    public static void Disable()
    {
        foreach (var progId in ProgIds)
        {
            using var shell = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{progId}\shell", writable: true);
            if (string.Equals(shell?.GetValue(null) as string, VerbName, StringComparison.OrdinalIgnoreCase))
            {
                shell!.DeleteValue(string.Empty, throwOnMissingValue: false);
            }
        }

        NotifyShell();
        Log.Info("Double-clicking .bat/.cmd files uses Windows' default again.");
    }

    /// <summary>Tells Explorer associations changed, so the new default applies without signing out.</summary>
    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
