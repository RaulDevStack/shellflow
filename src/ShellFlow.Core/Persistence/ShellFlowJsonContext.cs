using System.Text.Json.Serialization;
using ShellFlow.Core.Settings;
using ShellFlow.Core.Workspaces;

namespace ShellFlow.Core.Persistence;

/// <summary>Source-generated serializers: no reflection at startup, and the schema is explicit.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true,
    MaxDepth = 64)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(WorkspaceDocument))]
[JsonSerializable(typeof(AppState))]
public sealed partial class ShellFlowJsonContext : JsonSerializerContext
{
}

/// <summary>Small bits of state that are not user preferences.</summary>
public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;

    public string? LastWorkspaceId { get; set; }

    public WindowPlacement? Window { get; set; }
}

public sealed class WindowPlacement
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Maximized { get; set; }
}
