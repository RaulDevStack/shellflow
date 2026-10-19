using System.Reflection;
using ShellFlow.Core.Bridge;
using ShellFlow.Core.Commands;

namespace ShellFlow.Tests.Commands;

public sealed class DefaultCommandsTests
{
    [Fact]
    public void EveryCommandId_HasExactlyOneDefinition()
    {
        var ids = typeof(CommandIds).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetValue(null)!).ToList();
        Assert.Equal(ids.OrderBy(i => i), DefaultCommands.All.Select(d => d.Id).OrderBy(i => i));
    }

    [Fact]
    public void EveryDefaultShortcut_Parses()
    {
        foreach (var definition in DefaultCommands.All.Where(d => d.DefaultShortcut is not null))
        {
            var parts = definition.DefaultShortcut!.Split('|', StringSplitOptions.TrimEntries);
            Assert.True(parts.Length == CommandRegistry.ParseChords(definition.DefaultShortcut).Count, $"{definition.Id}: '{definition.DefaultShortcut}' contains an unparseable chord");
        }
    }

    [Fact]
    public void DefaultShortcuts_DoNotConflict()
    {
        var registry = new CommandRegistry();
        foreach (var definition in DefaultCommands.All)
        {
            registry.Register(definition, _ => { });
        }

        Assert.Empty(registry.ApplyOverrides(null));
    }

    [Fact]
    public void DefaultShortcuts_AvoidAltGrAndPlainKeys()
    {
        // Ctrl+Alt chords collide with AltGr characters on many layouts; unmodified keys belong to the shell.
        var chords = DefaultCommands.All.SelectMany(d => CommandRegistry.ParseChords(d.DefaultShortcut));
        Assert.DoesNotContain(chords, c => c.Ctrl && c.Alt);
        Assert.DoesNotContain(chords, c => !c.Ctrl && !c.Alt && !c.Shift && !c.Code.StartsWith('F'));
    }

    [Fact]
    public void CommandIds_AreAcceptedByTheRendererBridge()
    {
        Assert.All(DefaultCommands.All, d => Assert.True(BridgeProtocol.IsValidCommandId(d.Id), d.Id));
    }

    [Fact]
    public void CommandPalette_UsesCtrlShiftP()
    {
        Assert.Equal("Ctrl+Shift+P", DefaultCommands.Get(CommandIds.CommandPalette).DefaultShortcut);
    }
}
