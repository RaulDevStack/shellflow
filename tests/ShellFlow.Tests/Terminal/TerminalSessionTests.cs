using System.Text;
using ShellFlow.Terminal;

namespace ShellFlow.Tests.Terminal;

/// <summary>Integration tests that start real shells through ConPTY.</summary>
public sealed class TerminalSessionTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string WindowsPowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public async Task Cmd_EchoesCommandOutput()
    {
        await using var session = TerminalSession.Start(new TerminalLaunchOptions { Executable = Cmd, Arguments = "/d /q /k echo off", Columns = 100, Rows = 30 });
        var output = new OutputCollector(session);

        session.Write("echo shellflow-%COMPUTERNAME:~0,0%marker\r");

        Assert.True(await output.WaitForAsync("shellflow-marker", TimeSpan.FromSeconds(10)), output.Text);
    }

    [Fact]
    public async Task PowerShell_RunsCommandsAndEmitsAnsiColor()
    {
        await using var session = TerminalSession.Start(new TerminalLaunchOptions { Executable = WindowsPowerShell, Arguments = "-NoLogo -NoProfile", Columns = 120, Rows = 30 });
        var output = new OutputCollector(session);

        // The marker is concatenated at runtime so the echoed command line cannot satisfy the wait.
        session.Write("Write-Host ('Col' + 'ored-' + 'Output') -ForegroundColor Green\r");

        Assert.True(await output.WaitForAsync("Colored-Output", TimeSpan.FromSeconds(20)), output.Text);
        // ConPTY renders console attributes as SGR sequences; green is 32/92 or the indexed form 38;5;2 / 38;5;10.
        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(output.Text, @"\x1b\[(?:[0-9;]*;)?(?:32|92|38;5;(?:2|10))m[^\x1b]*Colored-Output"),
            output.Text.Replace("\x1b", "<ESC>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exit_ReportsExitCodeAndCompletesOutput()
    {
        await using var session = TerminalSession.Start(new TerminalLaunchOptions { Executable = Cmd, Arguments = "/d /q /c exit 7" });
        var output = new OutputCollector(session);

        var code = await session.Exited.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(7, code);
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(TerminalSessionState.Running, session.State);
    }

    [Fact]
    public async Task CtrlC_InterruptsRunningCommand()
    {
        await using var session = TerminalSession.Start(new TerminalLaunchOptions { Executable = Cmd, Arguments = "/d /q /k echo off" });
        var output = new OutputCollector(session);

        session.Write("ping -n 30 127.0.0.1\r");
        Assert.True(await output.WaitForAsync("127.0.0.1", TimeSpan.FromSeconds(10)), output.Text);
        await Task.Delay(1500);
        Assert.True(session.HasRunningCommand, "ping should be counted as a running command");

        session.Write("\x03");
        session.Write("echo after-cancel\r");

        Assert.True(await output.WaitForAsync("after-cancel", TimeSpan.FromSeconds(10)), output.Text);
        Assert.True(session.State == TerminalSessionState.Running, "the shell itself must survive Ctrl+C");
    }

    [Fact]
    public async Task Resize_IsVisibleToTheShell()
    {
        await using var session = TerminalSession.Start(new TerminalLaunchOptions { Executable = WindowsPowerShell, Arguments = "-NoLogo -NoProfile", Columns = 80, Rows = 24 });
        var output = new OutputCollector(session);

        session.Resize(133, 41);
        await Task.Delay(300);
        session.Write("Write-Output (\"size=\" + $Host.UI.RawUI.WindowSize.Width + \"x\" + $Host.UI.RawUI.WindowSize.Height)\r");

        Assert.True(await output.WaitForAsync("size=133x41", TimeSpan.FromSeconds(20)), output.Text);
        Assert.Equal(133, session.Columns);
        Assert.Equal(41, session.Rows);
    }

    [Fact]
    public async Task Unicode_RoundTripsThroughThePseudoConsole()
    {
        await using var session = TerminalSession.Start(new TerminalLaunchOptions { Executable = Cmd, Arguments = "/d /q /k chcp 65001 >nul & echo off" });
        var output = new OutputCollector(session);

        session.Write("echo héllo-wörld-✓\r");

        Assert.True(await output.WaitForAsync("héllo-wörld-✓", TimeSpan.FromSeconds(10)), output.Text);
    }

    [Fact]
    public async Task LongOutput_IsStreamedCompletely()
    {
        await using var session = TerminalSession.Start(new TerminalLaunchOptions { Executable = Cmd, Arguments = "/d /q /k echo off", Columns = 120, Rows = 30 });
        var output = new OutputCollector(session);

        session.Write("for /L %i in (1,1,5000) do @echo line-%i\r");

        Assert.True(await output.WaitForAsync("line-5000", TimeSpan.FromSeconds(30)), output.Text[^Math.Min(500, output.Text.Length)..]);
    }

    [Fact]
    public async Task Close_TerminatesShellAndIsIdempotent()
    {
        var session = TerminalSession.Start(new TerminalLaunchOptions { Executable = Cmd, Arguments = "/d /q /k echo off" });
        _ = new OutputCollector(session);
        var pid = session.ProcessId;

        await session.CloseAsync(TimeSpan.FromSeconds(2));
        await session.CloseAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TerminalSessionState.Closed, session.State);
        Assert.False(IsProcessAlive(pid), "the shell process should have exited");
    }

    [Fact]
    public async Task RepeatedOpenAndClose_DoesNotLeakProcesses()
    {
        var pids = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            var session = TerminalSession.Start(new TerminalLaunchOptions { Executable = Cmd, Arguments = "/d /q /k echo off" });
            _ = new OutputCollector(session);
            pids.Add(session.ProcessId);
            await session.DisposeAsync();
        }

        Assert.All(pids, pid => Assert.False(IsProcessAlive(pid)));
    }

    [Fact]
    public void Start_WithMissingExecutable_ThrowsActionableError()
    {
        var ex = Assert.Throws<TerminalStartException>(() => TerminalSession.Start(new TerminalLaunchOptions { Executable = @"C:\does-not-exist\nope.exe" }));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_WithMissingWorkingDirectory_FallsBackToUserProfile()
    {
        await using var session = TerminalSession.Start(new TerminalLaunchOptions { Executable = Cmd, Arguments = "/d /q /k echo off", WorkingDirectory = @"Z:\definitely\missing" });
        var output = new OutputCollector(session);

        session.Write("cd\r");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.True(await output.WaitForAsync(profile, TimeSpan.FromSeconds(10)), output.Text);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class OutputCollector
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _lock = new();

        public OutputCollector(TerminalSession session)
        {
            Completion = Task.Run(async () =>
            {
                await foreach (var chunk in session.Output.ReadAllAsync())
                {
                    lock (_lock)
                    {
                        _text.Append(chunk);
                    }
                }
            });
        }

        public Task Completion { get; }

        public string Text
        {
            get
            {
                lock (_lock)
                {
                    return _text.ToString();
                }
            }
        }

        // ConPTY may wrap or reposition text with escape sequences, so match on text with sequences stripped too.
        public async Task<bool> WaitForAsync(string needle, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var text = Text;
                if (text.Contains(needle, StringComparison.Ordinal) ||
                    System.Text.RegularExpressions.Regex.Replace(text, @"\x1b\[[0-9;?]*[A-Za-z]", string.Empty).Contains(needle, StringComparison.Ordinal))
                {
                    return true;
                }

                await Task.Delay(50);
            }

            return false;
        }
    }
}
