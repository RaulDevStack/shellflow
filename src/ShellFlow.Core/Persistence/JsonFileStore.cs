using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ShellFlow.Core.Persistence;

public enum LoadStatus
{
    Loaded,
    Missing,

    /// <summary>The file was unreadable; it was moved aside and defaults were used.</summary>
    Recovered,

    /// <summary>The file was written by a newer ShellFlow. It was read best-effort and must not be overwritten silently.</summary>
    NewerVersion,
}

public sealed record LoadResult<T>(T Value, LoadStatus Status, string? BackupPath = null, string? Error = null);

/// <summary>JSON persistence with atomic writes, corruption recovery and schema-version checks.</summary>
public static class JsonFileStore
{
    public const long MaxFileSize = 16 * 1024 * 1024;

    /// <param name="migrate">Upgrades a document from an older schema version to the current shape.</param>
    public static LoadResult<T> Load<T>(string path, JsonTypeInfo<T> typeInfo, int currentVersion, Func<T> createDefault, Func<JsonObject, int, JsonObject>? migrate = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(createDefault);
        if (!File.Exists(path))
        {
            return new LoadResult<T>(createDefault(), LoadStatus.Missing);
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileSize)
            {
                throw new InvalidDataException($"File is larger than {MaxFileSize / (1024 * 1024)} MB.");
            }

            var text = File.ReadAllText(path);
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 })
                as JsonObject ?? throw new InvalidDataException("The root of the file is not a JSON object.");

            var version = ReadVersion(node);
            if (version < currentVersion && migrate is not null)
            {
                node = migrate(node, version);
            }

            var value = node.Deserialize(typeInfo) ?? throw new InvalidDataException("The file is empty.");
            return new LoadResult<T>(value, version > currentVersion ? LoadStatus.NewerVersion : LoadStatus.Loaded);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or InvalidOperationException or FormatException)
        {
            var backup = MoveAside(path);
            Trace.TraceWarning($"ShellFlow: {Path.GetFileName(path)} could not be read ({ex.Message}); defaults are in use.");
            return new LoadResult<T>(createDefault(), LoadStatus.Recovered, backup, ex.Message);
        }
    }

    /// <summary>Writes to a temporary file in the same directory, then atomically replaces the target.</summary>
    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, typeInfo);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    internal static int ReadVersion(JsonObject node)
    {
        // Missing version means the earliest schema.
        return node.TryGetPropertyValue("schemaVersion", out var v) && v is JsonValue value && value.TryGetValue<int>(out var version)
            ? version
            : 1;
    }

    private static string? MoveAside(string path)
    {
        try
        {
            var backup = Path.Combine(
                Path.GetDirectoryName(path)!,
                $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}");
            File.Move(path, backup, overwrite: true);
            return backup;
        }
        catch (IOException ex)
        {
            Trace.TraceWarning($"ShellFlow: could not back up {path}: {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.TraceWarning($"ShellFlow: could not back up {path}: {ex.Message}");
            return null;
        }
    }
}
