using System.Text;

namespace ShellFlow.Core.Profiles;

/// <summary>Folders the cd picker shows: either the subfolders of <see cref="Base"/> or the list of drives.</summary>
public sealed record FolderListing(string? Base, string Prefix, IReadOnlyList<string> Entries, bool IsDriveList, string? Error)
{
    public static FolderListing Failed(string error) => new(null, string.Empty, [], false, error);
}

/// <summary>
/// Path logic behind "cd + Tab": turns what the user typed after <c>cd</c> into a folder to list, lists subfolders,
/// and formats the chosen folder for the shell (quoting and path style differ between PowerShell, CMD and Git Bash).
/// </summary>
public static class FolderCompletion
{
    public const int MaxEntries = 500;
    public const string NoWorkingDirectory = "nocwd";

    /// <summary>
    /// Splits a partial argument into the folder to list and the name prefix to filter by.
    /// "C:\Users\Ra" → base "C:\Users\", prefix "Ra". Relative paths resolve against <paramref name="cwd"/>.
    /// Returns the absolute base (with trailing separator) or null when it cannot be resolved.
    /// </summary>
    public static (string? Base, string Prefix, string? Error) Resolve(string partial, string? cwd, string home)
    {
        ArgumentNullException.ThrowIfNull(partial);
        var text = partial.Trim().Trim('"', '\'');
        text = FromPosix(text).Replace('/', '\\');

        var cut = text.LastIndexOf('\\');
        var baseText = cut >= 0 ? text[..(cut + 1)] : string.Empty;
        var prefix = cut >= 0 ? text[(cut + 1)..] : text;

        // "C:" alone means the root of C:.
        if (cut < 0 && text.Length == 2 && text[1] == ':' && char.IsAsciiLetter(text[0]))
        {
            return (char.ToUpperInvariant(text[0]) + @":\", string.Empty, null);
        }

        string? absolute;
        if (baseText.StartsWith('~'))
        {
            absolute = home + baseText[1..];
        }
        else if (IsRooted(baseText))
        {
            absolute = baseText;
        }
        else if (cwd is not null && IsRooted(cwd))
        {
            absolute = Path.Combine(cwd, baseText);
        }
        else
        {
            return (null, prefix, NoWorkingDirectory);
        }

        try
        {
            absolute = Path.GetFullPath(absolute);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, prefix, "invalid path");
        }

        return (WithSeparator(absolute), prefix, null);
    }

    /// <summary>Lists subfolders of an absolute folder (hidden + system folders such as $Recycle.Bin are skipped).</summary>
    public static FolderListing List(string folder, string prefix = "")
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists)
            {
                return FolderListing.Failed("not found");
            }

            var entries = info.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden })
                .Select(d => d.Name)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntries)
                .ToList();
            return new FolderListing(WithSeparator(info.FullName), prefix, entries, false, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return FolderListing.Failed(ex is UnauthorizedAccessException ? "access denied" : "unavailable");
        }
    }

    public static FolderListing Drives()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network or DriveType.Ram)
            .Select(d => d.Name)
            .ToList();
        return new FolderListing(null, string.Empty, drives, true, null);
    }

    /// <summary>
    /// Resolves a folder chosen in the picker. A trailing ".." at a drive root means "show the drives".
    /// </summary>
    public static (string? Folder, bool ShowDrives) Navigate(string requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var trimmed = requested.TrimEnd('\\', '/');
        if (trimmed.EndsWith("..", StringComparison.Ordinal))
        {
            var current = trimmed[..^2];
            if (IsRooted(current) && Path.GetPathRoot(Path.GetFullPath(current)) is { } root &&
                string.Equals(WithSeparator(Path.GetFullPath(current)), WithSeparator(root), StringComparison.OrdinalIgnoreCase))
            {
                return (null, true);
            }
        }

        if (!IsRooted(requested))
        {
            return (null, false);
        }

        try
        {
            return (WithSeparator(Path.GetFullPath(requested)), false);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, false);
        }
    }

    /// <summary>
    /// Text to put after "cd" for a folder. PowerShell gets single quotes (no $ expansion), CMD gets /d (switches drive)
    /// and double quotes, Git Bash gets a POSIX path in single quotes.
    /// </summary>
    public static string Format(string folder, ShellKind kind)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var path = WithSeparator(folder);
        return kind switch
        {
            ShellKind.Cmd => $"/d \"{path}\"",
            ShellKind.GitBash => $"'{ToPosix(path).Replace("'", "'\\''", StringComparison.Ordinal)}'",
            _ => $"'{path.Replace("'", "''", StringComparison.Ordinal)}'",
        };
    }

    /// <summary>"C:\Users\" → "/c/Users/".</summary>
    public static string ToPosix(string windowsPath)
    {
        if (windowsPath.Length >= 2 && windowsPath[1] == ':' && char.IsAsciiLetter(windowsPath[0]))
        {
            return "/" + char.ToLowerInvariant(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');
        }

        return windowsPath.Replace('\\', '/');
    }

    /// <summary>"/c/Users" → "C:\Users"; other text unchanged.</summary>
    public static string FromPosix(string path)
    {
        if (path.Length >= 2 && path[0] == '/' && char.IsAsciiLetter(path[1]) && (path.Length == 2 || path[2] == '/'))
        {
            var sb = new StringBuilder();
            sb.Append(char.ToUpperInvariant(path[1])).Append(':');
            sb.Append(path.Length > 2 ? path[2..].Replace('/', '\\') : "\\");
            return sb.ToString();
        }

        return path;
    }

    private static bool IsRooted(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/') ||
        path.StartsWith(@"\\", StringComparison.Ordinal);

    private static string WithSeparator(string path) => path.EndsWith('\\') ? path : path + '\\';
}
