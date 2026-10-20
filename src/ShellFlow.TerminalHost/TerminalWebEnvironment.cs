using Microsoft.Web.WebView2.Core;

namespace ShellFlow.TerminalHost;

/// <summary>
/// One WebView2 environment shared by every terminal pane, so all panes share one browser process
/// and one user-data folder instead of paying startup cost per pane.
/// </summary>
public static class TerminalWebEnvironment
{
    /// <summary>Virtual host that maps to the bundled renderer assets. ".example" is reserved and never resolves on the network.</summary>
    public const string HostName = "terminal.shellflow.example";
    public const string Origin = "https://" + HostName;
    public const string EntryUrl = Origin + "/index.html";

    private static readonly Lock Gate = new();
    private static Task<CoreWebView2Environment>? _environment;
    private static string? _userDataFolder;

    /// <summary>
    /// Folder holding the renderer page. WinUI places a referenced library's content under a folder named after
    /// the library; a flat layout (Assets\terminal) is accepted too.
    /// </summary>
    public static string AssetsFolder
    {
        get
        {
            var nested = Path.Combine(AppContext.BaseDirectory, "ShellFlow.TerminalHost", "Assets", "terminal");
            return Directory.Exists(nested) ? nested : Path.Combine(AppContext.BaseDirectory, "Assets", "terminal");
        }
    }

    /// <summary>Must be called before the first terminal is created.</summary>
    public static void Configure(string userDataFolder)
    {
        lock (Gate)
        {
            if (_environment is not null)
            {
                throw new InvalidOperationException("The WebView2 environment has already been created.");
            }

            _userDataFolder = userDataFolder;
        }
    }

    public static Task<CoreWebView2Environment> GetAsync()
    {
        lock (Gate)
        {
            return _environment ??= CreateAsync();
        }
    }

    /// <summary>Returns the installed WebView2 runtime version, or null if the runtime is missing.</summary>
    public static string? GetRuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FileNotFoundException)
        {
            return null;
        }
    }

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            // Keep the renderer lean: no background networking features the terminal never uses.
            AdditionalBrowserArguments = "--disable-background-networking --disable-sync --disable-component-update --no-first-run",
            AllowSingleSignOnUsingOSPrimaryAccount = false,
            EnableTrackingPrevention = true,
        };

        if (_userDataFolder is not null)
        {
            Directory.CreateDirectory(_userDataFolder);
        }

        return await CoreWebView2Environment.CreateWithOptionsAsync(null, _userDataFolder, options);
    }
}
