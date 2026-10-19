using System.Diagnostics;
using System.Text;

namespace ShellFlow.App.Services;

/// <summary>
/// Minimal file log in %LOCALAPPDATA%\ShellFlow\logs. Records lifecycle events and errors only —
/// never terminal input, output, clipboard contents or environment values.
/// </summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static string? _file;

    public static string? CurrentFile => _file;

    public static void Initialize(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _file = Path.Combine(directory, $"shellflow-{DateTime.Now:yyyyMMdd}.log");
            foreach (var old in new DirectoryInfo(directory).GetFiles("shellflow-*.log").OrderByDescending(f => f.Name).Skip(7))
            {
                old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"ShellFlow: logging disabled: {ex.Message}");
            _file = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        if (_file is null)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"ShellFlow: could not write log: {ex.Message}");
            }
        }
    }
}
