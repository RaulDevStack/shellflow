using System.Text.Json.Serialization;

namespace ShellFlow.Core.Bridge;

/// <summary>Messages native code sends to the renderer. Shell output is sent separately as a plain string.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConfigureRenderer), "config")]
[JsonDerivedType(typeof(PasteText), "paste")]
[JsonDerivedType(typeof(FindText), "find")]
[JsonDerivedType(typeof(ClearFind), "clearFind")]
[JsonDerivedType(typeof(FocusRenderer), "focus")]
[JsonDerivedType(typeof(RendererAction), "action")]
[JsonDerivedType(typeof(SessionStatus), "status")]
[JsonDerivedType(typeof(DirectoryListing), "dirs")]
[JsonDerivedType(typeof(CompletionMode), "completion")]
public abstract record RendererMessage;

/// <summary>Answer to a <see cref="DirectoryListRequest"/>. <see cref="Base"/> is null for the drive list.</summary>
public sealed record DirectoryListing(int Id, string? Base, string Prefix, IReadOnlyList<string> Entries, bool Drives, string? Error) : RendererMessage;

/// <summary>Turns the cd + Tab picker on or off for this pane.</summary>
public sealed record CompletionMode(bool Enabled) : RendererMessage;

public sealed record ConfigureRenderer(RendererOptions Options) : RendererMessage;

public sealed record PasteText(string Data) : RendererMessage;

public sealed record FindText(string Query, bool Backwards, bool CaseSensitive, bool Regex, bool WholeWord, bool Incremental) : RendererMessage;

public sealed record ClearFind : RendererMessage;

public sealed record FocusRenderer : RendererMessage;

/// <summary>Renderer-local actions: "clear", "selectAll", "copy", "scrollToBottom".</summary>
public sealed record RendererAction(string Action) : RendererMessage;

/// <summary>Shown as an overlay inside the renderer, for example "Process exited with code 1".</summary>
public sealed record SessionStatus(string Kind, string? Text) : RendererMessage;

public sealed record RendererOptions
{
    public required string FontFamily { get; init; }

    public required double FontSize { get; init; }

    public required double LineHeight { get; init; }

    public required string CursorStyle { get; init; }

    public required bool CursorBlink { get; init; }

    public required int Scrollback { get; init; }

    public required bool Gpu { get; init; }

    public required bool CopyOnSelect { get; init; }

    public required TerminalTheme Theme { get; init; }

    /// <summary>Chords the renderer must hand to native code instead of the shell.</summary>
    public required IReadOnlyList<RendererKeyBinding> KeyBindings { get; init; }
}

public sealed record RendererKeyBinding(string Code, bool Ctrl, bool Alt, bool Shift, string Command);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RendererMessage))]
public sealed partial class BridgeJsonContext : JsonSerializerContext
{
}
