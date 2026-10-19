using System.Text;
using ShellFlow.Core;
using ShellFlow.Core.Bridge;
using ShellFlow.Core.Profiles;
using ShellFlow.Terminal;

namespace ShellFlow.Tests.Profiles;

public sealed class ShellIntegrationTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", ShellKind.PowerShell)]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", ShellKind.PowerShell)]
    [InlineData(@"C:\Windows\System32\cmd.exe", ShellKind.Cmd)]
    [InlineData(@"C:\Program Files\Git\bin\bash.exe", ShellKind.GitBash)]
    [InlineData(@"C:\Windows\System32\wsl.exe", ShellKind.Wsl)]
    [InlineData(@"C:\tools\nu.exe", ShellKind.Unknown)]
    public void Detect_RecognizesShells(string path, ShellKind kind) => Assert.Equal(kind, ShellIntegration.Detect(path));

    [Fact]
    public void PowerShell_KeepsArgumentsAndAddsEncodedPromptHook()
    {
        var launch = ShellIntegration.Apply(ShellKind.PowerShell, "-NoLogo", null, null);

        Assert.True(launch.Integrated);
        Assert.StartsWith("-NoLogo -NoExit -EncodedCommand ", launch.Arguments, StringComparison.Ordinal);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(launch.Arguments!.Split(' ')[^1]));
        Assert.Contains("]9;9;", script, StringComparison.Ordinal);
        Assert.Contains("__ShellFlowPrompt", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-NoLogo -Command Get-Date")]
    [InlineData("-File script.ps1")]
    [InlineData("-NoExit -c something")]
    public void PowerShell_ProfilesThatRunTheirOwnCommandAreLeftAlone(string args)
    {
        var launch = ShellIntegration.Apply(ShellKind.PowerShell, args, null, null);
        Assert.False(launch.Integrated);
        Assert.Equal(args, launch.Arguments);
    }

    [Fact]
    public void Cmd_PrependsOscToThePrompt()
    {
        Assert.Equal("$e]9;9;$P$e\\$P$G", ShellIntegration.Apply(ShellKind.Cmd, null, null, null).Environment!["PROMPT"]);
        Assert.Equal("$e]9;9;$P$e\\[$T] $P$G", ShellIntegration.Apply(ShellKind.Cmd, null, null, "[$T] $P$G").Environment!["PROMPT"]);
    }

    [Fact]
    public void ExplicitProfileVariables_WinOverIntegration()
    {
        var env = new Dictionary<string, string?> { ["PROMPT"] = "mine>" };
        var launch = ShellIntegration.Apply(ShellKind.Cmd, null, env, null);
        Assert.False(launch.Integrated);
        Assert.Equal("mine>", launch.Environment!["PROMPT"]);
    }

    [Fact]
    public void GitBash_SetsPromptCommand()
    {
        var launch = ShellIntegration.Apply(ShellKind.GitBash, "--login -i", null, null);
        Assert.Contains("cygpath -w", launch.Environment!["PROMPT_COMMAND"], StringComparison.Ordinal);
        Assert.Equal("--login -i", launch.Arguments);
    }

    [Fact]
    public void Wsl_IsNotIntegrated() => Assert.False(ShellIntegration.Apply(ShellKind.Wsl, "-d Ubuntu", null, null).Integrated);

    [Fact]
    public async Task PowerShell_Integration_ReportsCurrentFolderThroughConPty()
    {
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var launch = ShellIntegration.Apply(ShellKind.PowerShell, "-NoLogo -NoProfile", null, null);
        await using var session = TerminalSession.Start(new TerminalLaunchOptions { Executable = ps, Arguments = launch.Arguments, Environment = launch.Environment });
        var output = new StringBuilder();
        var reader = Task.Run(async () =>
        {
            await foreach (var chunk in session.Output.ReadAllAsync())
            {
                lock (output)
                {
                    output.Append(chunk);
                }
            }
        });

        session.Write("Set-Location $env:SystemRoot\r");
        var expected = $"\x1b]9;9;\"{Environment.GetEnvironmentVariable("SystemRoot")}\"";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            lock (output)
            {
                if (output.ToString().Contains(expected, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            await Task.Delay(100);
        }

        lock (output)
        {
            Assert.Fail("OSC 9;9 for the new folder was not seen: " + output.ToString().Replace("\x1b", "<ESC>", StringComparison.Ordinal));
        }
    }
}

public sealed class FolderCompletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shellflow-cd-" + Guid.NewGuid().ToString("N")[..8]);

    public FolderCompletionTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Alpha"));
        Directory.CreateDirectory(Path.Combine(_root, "alpine"));
        Directory.CreateDirectory(Path.Combine(_root, "Beta Folder", "Inner"));
        File.WriteAllText(Path.Combine(_root, "file.txt"), "x");
        var hidden = Directory.CreateDirectory(Path.Combine(_root, "Hidden"));
        hidden.Attributes |= FileAttributes.Hidden;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Resolve_RelativePartialUsesWorkingDirectory()
    {
        var (folder, prefix, error) = FolderCompletion.Resolve("Beta Folder\\In", _root, @"C:\Users\me");
        Assert.Null(error);
        Assert.Equal(Path.Combine(_root, "Beta Folder") + "\\", folder);
        Assert.Equal("In", prefix);
    }

    [Fact]
    public void Resolve_EmptyPartialListsWorkingDirectory()
    {
        var (folder, prefix, _) = FolderCompletion.Resolve("", _root, @"C:\Users\me");
        Assert.Equal(_root + "\\", folder);
        Assert.Equal(string.Empty, prefix);
    }

    [Theory]
    [InlineData(@"C:\Windows\Sys", @"C:\Windows\", "Sys")]
    [InlineData("'C:\\Program Files\\Win", @"C:\Program Files\", "Win")]
    [InlineData("/c/Windows/Sys", @"C:\Windows\", "Sys")]
    [InlineData("C:", @"C:\", "")]
    [InlineData(@"C:\Windows\System32\..\Te", @"C:\Windows\", "Te")]
    public void Resolve_AbsoluteForms(string partial, string expectedBase, string expectedPrefix)
    {
        var (folder, prefix, _) = FolderCompletion.Resolve(partial, null, @"C:\Users\me");
        Assert.Equal(expectedBase, folder);
        Assert.Equal(expectedPrefix, prefix);
    }

    [Fact]
    public void Resolve_HomeTilde() => Assert.Equal(@"C:\Users\me\Documents\", FolderCompletion.Resolve(@"~\Documents\", null, @"C:\Users\me").Base);

    [Fact]
    public void Resolve_RelativeWithoutWorkingDirectory_AsksForFallback()
    {
        var (folder, _, error) = FolderCompletion.Resolve("src", null, @"C:\Users\me");
        Assert.Null(folder);
        Assert.Equal(FolderCompletion.NoWorkingDirectory, error);
    }

    [Fact]
    public void List_ReturnsSortedVisibleFoldersOnly()
    {
        var listing = FolderCompletion.List(_root, "al");
        Assert.Equal(["Alpha", "alpine", "Beta Folder"], listing.Entries);
        Assert.Equal("al", listing.Prefix);
        Assert.Equal(_root + "\\", listing.Base);
    }

    [Fact]
    public void List_MissingFolderReportsError() => Assert.NotNull(FolderCompletion.List(Path.Combine(_root, "nope")).Error);

    [Fact]
    public void Navigate_ParentAtDriveRootShowsDrives()
    {
        Assert.True(FolderCompletion.Navigate(@"C:\..").ShowDrives);
        Assert.Equal(@"C:\Windows\", FolderCompletion.Navigate(@"C:\Windows\System32\..").Folder);
        Assert.Null(FolderCompletion.Navigate("relative").Folder);
    }

    [Theory]
    [InlineData(ShellKind.PowerShell, @"C:\It's here", @"'C:\It''s here\'")]
    [InlineData(ShellKind.PowerShell, @"C:\$env", @"'C:\$env\'")]
    [InlineData(ShellKind.Cmd, @"D:\Program Files", "/d \"D:\\Program Files\\\"")]
    [InlineData(ShellKind.GitBash, @"C:\Users\me", "'/c/Users/me/'")]
    public void Format_QuotesForEachShell(ShellKind kind, string folder, string expected) => Assert.Equal(expected, FolderCompletion.Format(folder, kind));

    [Fact]
    public void Drives_IncludesSystemDrive() =>
        Assert.Contains(FolderCompletion.Drives().Entries, d => d.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!, StringComparison.OrdinalIgnoreCase));
}

public sealed class InstanceMessageTests
{
    [Fact]
    public async Task RoundTripsArguments()
    {
        var bytes = InstanceMessage.Encode(["--new-pane", "-d", @"D:\Mis cosas\ñ"]);
        using var stream = new MemoryStream(bytes);
        Assert.Equal(["--new-pane", "-d", @"D:\Mis cosas\ñ"], (await InstanceMessage.ReadAsync(stream, CancellationToken.None))!);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0 })]
    [InlineData(new byte[] { 255, 255, 255, 127 })]
    [InlineData(new byte[] { 4, 0, 0, 0, (byte)'n', (byte)'u', (byte)'l', (byte)'l' })]
    [InlineData(new byte[] { 3, 0, 0, 0, (byte)'{', (byte)'}', (byte)' ' })]
    [InlineData(new byte[] { 10, 0, 0 })]
    public async Task RejectsMalformedMessages(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        Assert.Null(await InstanceMessage.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void StartupArguments_ParseModes()
    {
        Assert.Equal(LaunchMode.NewPane, StartupArguments.Parse(["--new-pane", "-d", @"C:\"]).Mode);
        Assert.Equal(LaunchMode.NewTab, StartupArguments.Parse(["--new-tab"]).Mode);
        Assert.False(StartupArguments.Parse([]).RequestsTerminal);
        Assert.True(StartupArguments.Parse([@"C:\src"]).RequestsTerminal);
    }
}

public sealed class FolderBridgeTests
{
    [Fact]
    public void DirectoryMessages_Parse()
    {
        Assert.True(BridgeProtocol.TryParse("""{"type":"dirList","id":3,"partial":"Doc"}""", out var a, out _));
        Assert.Equal(new DirectoryListRequest(3, "Doc", null), a);
        Assert.True(BridgeProtocol.TryParse("""{"type":"dirList","id":4,"dir":"C:\\Users\\.."}""", out var b, out _));
        Assert.Equal(new DirectoryListRequest(4, null, @"C:\Users\.."), b);
        Assert.True(BridgeProtocol.TryParse("""{"type":"dirAccept","dir":"C:\\src","erase":3,"run":true}""", out var c, out _));
        Assert.Equal(new DirectoryAcceptMessage(@"C:\src", 3, true), c);
    }

    [Theory]
    [InlineData("""{"type":"dirList","id":1}""")]
    [InlineData("""{"type":"dirList","id":1,"partial":"a","dir":"C:\\"}""")]
    [InlineData("""{"type":"dirList","id":1,"dir":"C:\\a\u0007"}""")]
    [InlineData("""{"type":"dirAccept","dir":"C:\\","erase":-1,"run":true}""")]
    [InlineData("""{"type":"dirAccept","dir":"C:\\","erase":2,"run":"yes"}""")]
    public void InvalidDirectoryMessages_AreRejected(string json) => Assert.False(BridgeProtocol.TryParse(json, out _, out _));
}
