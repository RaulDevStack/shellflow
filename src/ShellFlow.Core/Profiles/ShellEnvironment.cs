using System.Diagnostics;
using System.Text;

namespace ShellFlow.Core.Profiles;

/// <summary>The parts of the machine that shell detection depends on, abstracted for tests.</summary>
public interface IShellEnvironment
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    string? GetVariable(string name);

    string ExpandVariables(string value);

    /// <summary>Installed WSL distributions, or an empty list when WSL is unavailable.</summary>
    IReadOnlyList<string> GetWslDistributions();
}

public sealed class SystemShellEnvironment : IShellEnvironment
{
    public static SystemShellEnvironment Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string ExpandVariables(string value) => Environment.ExpandEnvironmentVariables(value);

    public IReadOnlyList<string> GetWslDistributions()
    {
        var wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        if (!File.Exists(wsl))
        {
            return [];
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(wsl, "--list --quiet")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    // wsl.exe writes its list as UTF-16LE.
                    StandardOutputEncoding = Encoding.Unicode,
                },
            };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(4000))
            {
                process.Kill(entireProcessTree: true);
                return [];
            }

            return process.ExitCode == 0 ? ProfileDetector.ParseWslList(outputTask.GetAwaiter().GetResult()) : [];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Trace.TraceWarning($"ShellFlow: WSL detection failed: {ex.Message}");
            return [];
        }
    }
}
