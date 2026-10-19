namespace ShellFlow.Terminal;

/// <summary>Everything needed to start a shell inside a pseudo console.</summary>
public sealed record TerminalLaunchOptions
{
    /// <summary>Fully qualified path to the executable. Resolve PATH lookups before starting a session.</summary>
    public required string Executable { get; init; }

    /// <summary>Raw Windows command-line arguments appended after the quoted executable.</summary>
    public string? Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>Variables to add or override. A null value removes the variable from the child environment.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    public int Columns { get; init; } = 120;

    public int Rows { get; init; } = 30;
}

public sealed class TerminalStartException : Exception
{
    public TerminalStartException(string message, int? win32Error = null, Exception? inner = null)
        : base(message, inner)
    {
        Win32Error = win32Error;
    }

    public int? Win32Error { get; }
}
