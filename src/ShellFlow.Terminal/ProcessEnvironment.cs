using System.Collections;
using System.Text;

namespace ShellFlow.Terminal;

/// <summary>Builds command lines and Unicode environment blocks for CreateProcessW.</summary>
public static class ProcessEnvironment
{
    public static string BuildCommandLine(string executable, string? arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var quoted = executable.StartsWith('"') ? executable : $"\"{executable}\"";
        return string.IsNullOrWhiteSpace(arguments) ? quoted : $"{quoted} {arguments.Trim()}";
    }

    /// <summary>Merges the current process environment with overrides. Keys compare case-insensitively, like Windows.</summary>
    public static SortedDictionary<string, string> Merge(IDictionary current, IReadOnlyDictionary<string, string?>? overrides)
    {
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in current)
        {
            if (entry.Key is string key && entry.Value is string value && IsValidName(key))
            {
                merged[key] = value;
            }
        }

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (!IsValidName(key))
                {
                    continue;
                }

                if (value is null)
                {
                    merged.Remove(key);
                }
                else
                {
                    merged[key] = value;
                }
            }
        }

        return merged;
    }

    /// <summary>Serializes "KEY=VALUE\0...\0\0" as expected with CREATE_UNICODE_ENVIRONMENT.</summary>
    public static string ToEnvironmentBlock(SortedDictionary<string, string> variables)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in variables)
        {
            if (value.Contains('\0'))
            {
                continue;
            }

            sb.Append(key).Append('=').Append(value).Append('\0');
        }

        sb.Append('\0');
        return sb.ToString();
    }

    // "=C:" style drive variables start with '=', which is allowed; names may not contain '=' elsewhere or NUL.
    private static bool IsValidName(string key) =>
        key.Length > 0 && key.IndexOf('=', 1) < 0 && !key.Contains('\0');
}
