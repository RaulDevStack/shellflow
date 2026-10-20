using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace ShellFlow.App.Services;

/// <summary>
/// Administrator support. A non-elevated process cannot host an elevated console, so (like Windows Terminal)
/// ShellFlow opens a separate elevated window. That window is its own instance with its own WebView2 data folder,
/// because Edge WebView2 cannot share a user-data folder between elevated and non-elevated processes.
/// </summary>
public static class Elevation
{
    private const int ErrorCancelled = 1223; // The user declined the UAC prompt.

    /// <summary>True when ShellFlow runs with an elevated (administrator) token.</summary>
    public static bool IsElevated { get; } = DetectElevation();

    /// <summary>
    /// Starts an elevated ShellFlow window through UAC. Returns false if the user cancelled the prompt.
    /// </summary>
    public static bool LaunchElevated(string? directory, string? profileId)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            args.Add("-d");
            args.Add(directory);
        }

        if (!string.IsNullOrWhiteSpace(profileId))
        {
            args.Add("-p");
            args.Add(profileId);
        }

        return LaunchElevated(args);
    }

    /// <summary>Starts ShellFlow elevated with the given command-line arguments. Returns false if UAC was cancelled.</summary>
    public static bool LaunchElevated(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path."))
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            Process.Start(start)?.Dispose();
            Log.Info("Started an administrator window.");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Info("Administrator window cancelled at the UAC prompt.");
            return false;
        }
    }

    private static bool DetectElevation()
    {
        using var identity = WindowsIdentity.GetCurrent();
        // With UAC, an administrator's normal (filtered) token is not in the role; only the elevated token is.
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
