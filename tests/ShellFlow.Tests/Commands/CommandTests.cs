using ShellFlow.Core.Commands;

namespace ShellFlow.Tests.Commands;

public sealed class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Shift+P", true, false, true, "KeyP")]
    [InlineData("ctrl+shift+p", true, false, true, "KeyP")]
    [InlineData("Alt+Left", false, true, false, "ArrowLeft")]
    [InlineData("Ctrl+Comma", true, false, false, "Comma")]
    [InlineData("Ctrl+,", true, false, false, "Comma")]
    [InlineData("Ctrl++", true, false, false, "Equal")]
    [InlineData("Ctrl+0", true, false, false, "Digit0")]
    [InlineData("Shift+Insert", false, false, true, "Insert")]
    [InlineData("F11", false, false, false, "F11")]
    [InlineData("Ctrl+Shift+BracketRight", true, false, true, "BracketRight")]
    public void Parse_ProducesDomCodes(string text, bool ctrl, bool alt, bool shift, string code)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(new KeyChord(ctrl, alt, shift, code), chord);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+P+Q")]
    [InlineData("Hyper+P")]
    [InlineData("F25")]
    public void Parse_RejectsInvalidChords(string text)
    {
        Assert.False(KeyChord.TryParse(text, out _));
    }

    [Theory]
    [InlineData("ctrl+shift+p", "Ctrl+Shift+P")]
    [InlineData("Shift+Alt+Ctrl+Left", "Ctrl+Alt+Shift+Left")]
    [InlineData("Ctrl+Comma", "Ctrl+,")]
    [InlineData("Ctrl+Equal", "Ctrl+=")]
    public void ToString_IsCanonicalAndReparseable(string input, string expected)
    {
        var chord = KeyChord.Parse(input);
        Assert.Equal(expected, chord.ToString());
        Assert.Equal(chord, KeyChord.Parse(chord.ToString()));
    }

    [Fact]
    public void IsKnownCode_ValidatesRendererCodes()
    {
        Assert.True(KeyChord.IsKnownCode("KeyA"));
        Assert.True(KeyChord.IsKnownCode("ArrowUp"));
        Assert.False(KeyChord.IsKnownCode("KeyAA"));
        Assert.False(KeyChord.IsKnownCode("<script>"));
    }
}

public sealed class CommandRegistryTests
{
    [Fact]
    public void Execute_RunsHandlerOnceAndRespectsCanExecute()
    {
        var registry = new CommandRegistry();
        var count = 0;
        var enabled = false;
        registry.Register(new CommandDefinition("tab.new", "New tab", "Tabs", "Ctrl+Shift+T"), _ => count++, () => enabled);

        Assert.False(registry.TryExecute("tab.new"));
        enabled = true;
        Assert.True(registry.TryExecute("tab.new"));
        Assert.False(registry.TryExecute("does.notExist"));
        Assert.Equal(1, count);
    }

    [Fact]
    public void Register_DuplicateId_Throws()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDefinition("a.b", "A", "X"), _ => { });
        Assert.Throws<InvalidOperationException>(() => registry.Register(new CommandDefinition("a.b", "A2", "X"), _ => { }));
    }

    [Fact]
    public void Shortcuts_SupportAliasesAndLookup()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDefinition("terminal.paste", "Paste", "Terminal", "Ctrl+Shift+V | Ctrl+V | Shift+Insert"), _ => { });

        Assert.Equal(3, registry.GetShortcuts("terminal.paste").Count);
        Assert.Equal("Ctrl+Shift+V", registry.GetShortcut("terminal.paste").ToString());
        Assert.Equal("terminal.paste", registry.FindCommand(KeyChord.Parse("Shift+Insert")));
    }

    [Fact]
    public void Overrides_WinConflictsAndCanUnbind()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDefinition("pane.splitRight", "Split right", "Panes", "Ctrl+Shift+E"), _ => { });
        registry.Register(new CommandDefinition("tab.new", "New tab", "Tabs", "Ctrl+Shift+T"), _ => { });

        var conflicts = registry.ApplyOverrides(new Dictionary<string, string>
        {
            ["tab.new"] = "Ctrl+Shift+E",
            ["pane.splitRight"] = string.Empty,
        });

        Assert.Empty(conflicts);
        Assert.Equal("tab.new", registry.FindCommand(KeyChord.Parse("Ctrl+Shift+E")));
        Assert.Null(registry.GetShortcut("pane.splitRight"));
        Assert.Null(registry.FindCommand(KeyChord.Parse("Ctrl+Shift+T")));
    }

    [Fact]
    public void ConflictingDefaults_AreReported()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDefinition("a.one", "One", "X", "Ctrl+K"), _ => { });
        registry.Register(new CommandDefinition("a.two", "Two", "X"), _ => { });

        var conflicts = registry.ApplyOverrides(new Dictionary<string, string> { ["a.two"] = "Ctrl+K", ["a.one"] = "Ctrl+K" });

        Assert.Single(conflicts);
    }

    [Fact]
    public void HandlerException_IsReportedNotThrown()
    {
        var registry = new CommandRegistry();
        CommandFailedEventArgs? failure = null;
        registry.CommandFailed += (_, e) => failure = e;
        registry.Register(new CommandDefinition("a.boom", "Boom", "X"), _ => throw new InvalidOperationException("nope"));

        Assert.False(registry.TryExecute("a.boom"));
        Assert.Equal("a.boom", failure?.Command.Id);
    }
}

public sealed class FuzzyMatcherTests
{
    [Fact]
    public void Substring_RanksAboveScattered()
    {
        var direct = FuzzyMatcher.Score("split", "Split right");
        var scattered = FuzzyMatcher.Score("split", "Select pane in tab");
        Assert.NotNull(direct);
        Assert.True(scattered is null || direct > scattered);
    }

    [Fact]
    public void WordInitials_Match()
    {
        Assert.NotNull(FuzzyMatcher.Score("sd", "Split down"));
        Assert.NotNull(FuzzyMatcher.Score("np", "Focus next pane"));
    }

    [Fact]
    public void NonMatching_ReturnsNull()
    {
        Assert.Null(FuzzyMatcher.Score("xyz", "New tab"));
    }

    [Fact]
    public void EmptyQuery_MatchesEverything()
    {
        Assert.Equal(0, FuzzyMatcher.Score("  ", "Anything"));
    }
}
