using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ShellFlow.App.Services;
using ShellFlow.Core.Persistence;

namespace ShellFlow.App;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN): before any UI starts, hand the launch to an already running
/// ShellFlow, so there is only ever one window.
/// </summary>
public static class Program
{
    internal static SingleInstance? Instance { get; private set; }

    /// <summary>Command-line arguments for this launch, without internal flags such as --elevate.</summary>
    internal static IReadOnlyList<string> Arguments { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var paths = AppPaths.Default;
        Log.Initialize(paths.LogsDirectory);

        // Run by the uninstaller: restore Windows' default for .bat/.cmd before ShellFlow's files disappear.
        if (args.Length == 1 && args[0] == "--unregister-scripts")
        {
            ScriptAssociation.Disable();
            return 0;
        }

        // "as administrator" entries in Explorer: re-launch through UAC (Explorer verbs cannot elevate by themselves).
        var (elevate, remaining) = Core.StartupArguments.ExtractElevate(args);
        if (elevate && !Elevation.IsElevated)
        {
            try
            {
                Elevation.LaunchElevated(remaining);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Error("Could not start ShellFlow as administrator", ex);
            }

            return 0;
        }

        Arguments = remaining;
        Instance = SingleInstance.Acquire(paths, remaining);
        if (Instance is null)
        {
            Log.Info("Forwarded launch to the running ShellFlow window.");
            return 0;
        }

        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        Instance.Dispose();
        return 0;
    }
}
