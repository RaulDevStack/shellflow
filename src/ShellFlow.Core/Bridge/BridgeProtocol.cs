using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShellFlow.Core.Bridge;

/// <summary>Messages the terminal renderer (untrusted JavaScript) may send to native code.</summary>
public abstract record BridgeMessage;

/// <summary>The renderer finished loading and measured its grid.</summary>
public sealed record ReadyMessage(int Columns, int Rows) : BridgeMessage;

/// <summary>Keystrokes or pasted text destined for the pane's own shell.</summary>
public sealed record InputMessage(string Data) : BridgeMessage;

public sealed record ResizeMessage(int Columns, int Rows) : BridgeMessage;

/// <summary>The renderer finished writing this many characters of output (flow control).</summary>
public sealed record AckMessage(int Characters) : BridgeMessage;

/// <summary>A key chord bound to an application command was pressed while the terminal had focus.</summary>
public sealed record CommandMessage(string CommandId) : BridgeMessage;

public sealed record CopyMessage(string Text) : BridgeMessage;

public sealed record TitleMessage(string Title) : BridgeMessage;

/// <summary>The shell reported its working directory through OSC 7 or OSC 9;9.</summary>
public sealed record WorkingDirectoryMessage(string Path) : BridgeMessage;

public sealed record FocusMessage : BridgeMessage;

public sealed record SearchResultMessage(int Index, int Count) : BridgeMessage;

public sealed record RendererInfoMessage(string Renderer) : BridgeMessage;

public sealed record RendererErrorMessage(string Message) : BridgeMessage;

/// <summary>
/// cd picker: list folders for what the user typed after cd (<see cref="Partial"/>) or for a folder the picker is
/// browsing (<see cref="Directory"/>, absolute; may end in ".." to go up). Exactly one of the two is set.
/// </summary>
public sealed record DirectoryListRequest(int Id, string? Partial, string? Directory) : BridgeMessage;

/// <summary>cd picker: replace the typed argument (<see cref="Erase"/> characters) with <see cref="Directory"/> and optionally run.</summary>
public sealed record DirectoryAcceptMessage(string Directory, int Erase, bool Run) : BridgeMessage;

/// <summary>
/// Parses and validates renderer messages. Anything unexpected is rejected: unknown types, wrong field types,
/// oversized payloads and out-of-range numbers. There is deliberately no message that runs arbitrary commands,
/// opens URLs or touches files — the renderer can only talk to its own pane's shell and to the registered
/// command list, which native code checks again before executing anything.
/// </summary>
public static partial class BridgeProtocol
{
    public const int MaxMessageLength = 6 * 1024 * 1024;
    public const int MaxInputLength = 64 * 1024;
    public const int MaxCopyLength = 4 * 1024 * 1024;
    public const int MaxTitleLength = 256;
    public const int MaxPathLength = 4096;
    public const int MaxErrorLength = 2048;
    public const int MaxColumns = 2000;
    public const int MaxRows = 1000;

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 4 };

    [GeneratedRegex("^[a-z]{2,16}\\.[A-Za-z0-9]{2,40}$")]
    private static partial Regex CommandIdPattern();

    public static bool TryParse(string? json, [NotNullWhen(true)] out BridgeMessage? message, out string? error)
    {
        message = null;
        error = null;

        if (string.IsNullOrEmpty(json))
        {
            error = "empty message";
            return false;
        }

        if (json.Length > MaxMessageLength)
        {
            error = "message too large";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                error = "missing type";
                return false;
            }

            message = typeElement.GetString() switch
            {
                "ready" => new ReadyMessage(Int(root, "cols", 1, MaxColumns), Int(root, "rows", 1, MaxRows)),
                "input" => new InputMessage(Str(root, "data", MaxInputLength, allowEmpty: false)),
                "resize" => new ResizeMessage(Int(root, "cols", 1, MaxColumns), Int(root, "rows", 1, MaxRows)),
                "ack" => new AckMessage(Int(root, "chars", 0, int.MaxValue)),
                "command" => new CommandMessage(CommandId(root)),
                "copy" => new CopyMessage(Str(root, "text", MaxCopyLength, allowEmpty: false)),
                "title" => new TitleMessage(StripControl(Str(root, "title", MaxTitleLength * 4, allowEmpty: true), MaxTitleLength)),
                "cwd" => new WorkingDirectoryMessage(WorkingDirectory(root)),
                "focus" => new FocusMessage(),
                "searchResult" => new SearchResultMessage(Int(root, "index", -1, int.MaxValue), Int(root, "count", 0, int.MaxValue)),
                "renderer" => new RendererInfoMessage(Str(root, "renderer", 16, allowEmpty: false) is "webgl" or "dom" ? root.GetProperty("renderer").GetString()! : throw new BridgeValidationException("unknown renderer")),
                "error" => new RendererErrorMessage(StripControl(Str(root, "message", MaxErrorLength * 4, allowEmpty: true), MaxErrorLength)),
                "dirList" => DirectoryList(root),
                "dirAccept" => new DirectoryAcceptMessage(PathText(root, "dir", allowEmpty: false)!, Int(root, "erase", 0, MaxPathLength), Bool(root, "run")),
                _ => throw new BridgeValidationException("unknown message type"),
            };
            return true;
        }
        catch (JsonException)
        {
            error = "malformed JSON";
            return false;
        }
        catch (BridgeValidationException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool IsValidCommandId(string? id) => id is not null && CommandIdPattern().IsMatch(id);

    private static DirectoryListRequest DirectoryList(JsonElement root)
    {
        var id = Int(root, "id", 0, int.MaxValue);
        var partial = root.TryGetProperty("partial", out _) ? PathText(root, "partial", allowEmpty: true) : null;
        var dir = root.TryGetProperty("dir", out _) ? PathText(root, "dir", allowEmpty: false) : null;
        if ((partial is null) == (dir is null))
        {
            throw new BridgeValidationException("exactly one of 'partial' or 'dir' is required");
        }

        return new DirectoryListRequest(id, partial, dir);
    }

    private static string? PathText(JsonElement root, string name, bool allowEmpty)
    {
        var value = Str(root, name, MaxPathLength, allowEmpty);
        return value.Any(char.IsControl) ? throw new BridgeValidationException($"'{name}' contains control characters") : value;
    }

    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : throw new BridgeValidationException($"'{name}' must be a boolean");

    private static string CommandId(JsonElement root)
    {
        var id = Str(root, "id", 64, allowEmpty: false);
        return IsValidCommandId(id) ? id : throw new BridgeValidationException("invalid command id");
    }

    private static string WorkingDirectory(JsonElement root)
    {
        var path = Str(root, "path", MaxPathLength, allowEmpty: false);
        if (path.Any(char.IsControl))
        {
            throw new BridgeValidationException("invalid path");
        }

        // Accept drive paths, UNC paths and POSIX paths (WSL/Git Bash). Nothing here is used to open files;
        // the value is only displayed and used as the starting directory hint for new panes.
        var looksValid = path.StartsWith('/') ||
                         path.StartsWith(@"\\", StringComparison.Ordinal) ||
                         (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));
        return looksValid ? path : throw new BridgeValidationException("invalid path");
    }

    private static int Int(JsonElement root, string name, int min, int max)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw new BridgeValidationException($"'{name}' must be an integer");
        }

        return value < min || value > max ? throw new BridgeValidationException($"'{name}' is out of range") : value;
    }

    private static string Str(JsonElement root, string name, int maxLength, bool allowEmpty)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new BridgeValidationException($"'{name}' must be a string");
        }

        var value = element.GetString()!;
        if (value.Length > maxLength)
        {
            throw new BridgeValidationException($"'{name}' is too long");
        }

        return !allowEmpty && value.Length == 0 ? throw new BridgeValidationException($"'{name}' is empty") : value;
    }

    private static string StripControl(string value, int maxLength)
    {
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }

    private sealed class BridgeValidationException(string message) : Exception(message);
}
