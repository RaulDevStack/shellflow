namespace ShellFlow.Core.Commands;

/// <param name="DefaultShortcut">One chord, or several separated by '|' (the first is shown in menus).</param>
public sealed record CommandDefinition(string Id, string Title, string Category, string? DefaultShortcut = null, string? Glyph = null, bool ShowInPalette = true)
{
    /// <summary>Extra words that should match in the command palette.</summary>
    public string? Keywords { get; init; }
}

/// <summary>
/// The single place where application actions live. Shortcuts, toolbar buttons, menus and the command palette
/// all execute commands through this registry, so each action is implemented exactly once.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private Dictionary<KeyChord, string> _chordMap = [];
    private Dictionary<string, IReadOnlyList<KeyChord>> _shortcuts = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string>? _overrides;

    public event EventHandler? ShortcutsChanged;

    /// <summary>Raised when a command handler throws. The exception is reported instead of crashing the app.</summary>
    public event EventHandler<CommandFailedEventArgs>? CommandFailed;

    public IEnumerable<CommandDefinition> Definitions => _order.Select(id => _entries[id].Definition);

    public IReadOnlyDictionary<KeyChord, string> ShortcutMap => _chordMap;

    public void Register(CommandDefinition definition, Action<object?> execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);
        if (!_entries.TryAdd(definition.Id, new Entry(definition, execute, canExecute)))
        {
            throw new InvalidOperationException($"Command '{definition.Id}' is already registered.");
        }

        _order.Add(definition.Id);
        RebuildShortcuts();
    }

    public bool Contains(string id) => _entries.ContainsKey(id);

    public CommandDefinition? Get(string id) => _entries.TryGetValue(id, out var entry) ? entry.Definition : null;

    public bool CanExecute(string id) => _entries.TryGetValue(id, out var entry) && (entry.CanExecute?.Invoke() ?? true);

    public bool TryExecute(string id, object? parameter = null)
    {
        if (!_entries.TryGetValue(id, out var entry) || !(entry.CanExecute?.Invoke() ?? true))
        {
            return false;
        }

        try
        {
            entry.Execute(parameter);
            return true;
        }
        catch (Exception ex) when (CommandFailed is not null)
        {
            CommandFailed.Invoke(this, new CommandFailedEventArgs(entry.Definition, ex));
            return false;
        }
    }

    /// <summary>The primary shortcut shown in the UI.</summary>
    public KeyChord? GetShortcut(string id) => _shortcuts.TryGetValue(id, out var chords) && chords.Count > 0 ? chords[0] : null;

    public IReadOnlyList<KeyChord> GetShortcuts(string id) => _shortcuts.TryGetValue(id, out var chords) ? chords : [];

    public string? FindCommand(KeyChord chord) => _chordMap.TryGetValue(chord, out var id) ? id : null;

    /// <summary>Applies user overrides on top of default shortcuts and returns descriptions of any conflicts.</summary>
    public IReadOnlyList<string> ApplyOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        _overrides = overrides;
        var conflicts = RebuildShortcuts();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        return conflicts;
    }

    public static IReadOnlyList<KeyChord> ParseChords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var chords = new List<KeyChord>();
        foreach (var part in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (KeyChord.TryParse(part, out var chord) && !chords.Contains(chord))
            {
                chords.Add(chord);
            }
        }

        return chords;
    }

    private List<string> RebuildShortcuts()
    {
        var conflicts = new List<string>();
        var shortcuts = new Dictionary<string, IReadOnlyList<KeyChord>>(StringComparer.Ordinal);
        var map = new Dictionary<KeyChord, string>();

        bool IsOverridden(string id) => _overrides is not null && _overrides.TryGetValue(id, out var o) && !string.IsNullOrEmpty(o);

        // User bindings claim their chords first so they win over defaults.
        foreach (var id in _order.OrderBy(id => IsOverridden(id) ? 0 : 1))
        {
            var definition = _entries[id].Definition;
            var text = _overrides is not null && _overrides.TryGetValue(id, out var overridden) ? overridden : definition.DefaultShortcut;
            var accepted = new List<KeyChord>();
            foreach (var chord in ParseChords(text))
            {
                if (map.TryGetValue(chord, out var owner))
                {
                    conflicts.Add($"{chord} is assigned to both \"{_entries[owner].Definition.Title}\" and \"{definition.Title}\"; keeping \"{_entries[owner].Definition.Title}\".");
                    continue;
                }

                map[chord] = id;
                accepted.Add(chord);
            }

            shortcuts[id] = accepted;
        }

        _shortcuts = shortcuts;
        _chordMap = map;
        return conflicts;
    }

    private sealed record Entry(CommandDefinition Definition, Action<object?> Execute, Func<bool>? CanExecute);
}

public sealed class CommandFailedEventArgs(CommandDefinition command, Exception exception) : EventArgs
{
    public CommandDefinition Command { get; } = command;

    public Exception Exception { get; } = exception;
}
