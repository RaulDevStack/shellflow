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
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path."))
        {
            UseShellExecute = true,
            Verb = "runas",
        };

        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(directory);
        }

        if (!string.IsNullOrWhiteSpace(profileId))
        {
            start.ArgumentList.Add("-p");
            start.ArgumentList.Add(profileId);
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
