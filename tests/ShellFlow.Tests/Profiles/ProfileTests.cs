using ShellFlow.Core.Profiles;
using ShellFlow.Terminal;

namespace ShellFlow.Tests.Profiles;

/// <summary>A fake machine so detection can be tested for configurations other than the build machine's.</summary>
internal sealed class FakeShellEnvironment : IShellEnvironment
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows",
        ["ProgramFiles"] = @"C:\Program Files",
        ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
        ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local",
        ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
        ["PATH"] = @"C:\Windows\System32;C:\tools",
    };

    public List<string> Distributions { get; } = [];

    public bool FileExists(string path) => Files.Contains(path);

    public bool DirectoryExists(string path) => Directories.Contains(path);

    public string? GetVariable(string name) => Variables.GetValueOrDefault(name);

    public string ExpandVariables(string value)
    {
        foreach (var (key, v) in Variables)
        {
            value = value.Replace($"%{key}%", v, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    public IReadOnlyList<string> GetWslDistributions() => Distributions;
}

public sealed class ProfileDetectorTests
{
    [Fact]
    public void MinimalMachine_DetectsOnlyWhatExists()
    {
        var env = new FakeShellEnvironment();
        env.Files.Add(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
        env.Files.Add(@"C:\Windows\System32\cmd.exe");

        var profiles = ProfileDetector.Detect(env);

        Assert.Equal([ProfileDetector.WindowsPowerShellId, ProfileDetector.CmdId], profiles.Select(p => p.Id));
        Assert.All(profiles, p => Assert.Equal(ProfileSource.Detected, p.Source));
    }

    [Fact]
    public void FullMachine_DetectsPwshGitBashAndWsl()
    {
        var env = new FakeShellEnvironment();
        env.Files.Add(@"C:\Program Files\PowerShell\7\pwsh.exe");
        env.Files.Add(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
        env.Files.Add(@"C:\Windows\System32\cmd.exe");
        env.Files.Add(@"C:\Program Files\Git\bin\bash.exe");
        env.Files.Add(@"C:\Windows\System32\wsl.exe");
        env.Distributions.Add("Ubuntu-24.04");

        var profiles = ProfileDetector.Detect(env);

        Assert.Equal(["pwsh", "windows-powershell", "cmd", "git-bash", "wsl-ubuntu-24-04"], profiles.Select(p => p.Id));
        var wsl = profiles[^1];
        Assert.Equal("Ubuntu-24.04", wsl.Name);
        Assert.Contains("-d \"Ubuntu-24.04\"", wsl.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void WslIsSkippedWhenNotInstalled()
    {
        var env = new FakeShellEnvironment();
        env.Distributions.Add("Ubuntu");
        Assert.Empty(ProfileDetector.Detect(env));
    }

    [Fact]
    public void ParseWslList_HandlesUtf16NullsAndFiltersInternalDistros()
    {
        var output = "Ubuntu\r\n\0docker-desktop\r\ndocker-desktop-data\r\nDebian\r\n\r\nUbuntu\r\nbad\"name\r\n";
        Assert.Equal(["Ubuntu", "Debian"], ProfileDetector.ParseWslList(output));
    }
}

public sealed class ProfileResolverTests
{
    [Fact]
    public void ResolveExecutable_SearchesPathWithPathExt()
    {
        var env = new FakeShellEnvironment();
        env.Files.Add(@"C:\tools\nu.exe");

        Assert.Equal(@"C:\tools\nu.exe", ProfileResolver.ResolveExecutable("nu", env));
        Assert.Equal(@"C:\tools\nu.exe", ProfileResolver.ResolveExecutable("nu.exe", env));
        Assert.Null(ProfileResolver.ResolveExecutable("missing", env));
        Assert.Null(ProfileResolver.ResolveExecutable(@"tools\nu.exe", env));
        Assert.Null(ProfileResolver.ResolveExecutable("   ", env));
    }

    [Fact]
    public void ResolveExecutable_ExpandsVariablesAndQuotes()
    {
        var env = new FakeShellEnvironment();
        env.Files.Add(@"C:\Program Files\Git\bin\bash.exe");
        Assert.Equal(@"C:\Program Files\Git\bin\bash.exe", ProfileResolver.ResolveExecutable("\"%ProgramFiles%\\Git\\bin\\bash.exe\"", env));
    }

    [Fact]
    public void Resolve_MissingExecutable_IsActionableError()
    {
        var env = new FakeShellEnvironment();
        var resolved = ProfileResolver.Resolve(new ShellProfile { Id = "x", Name = "X", Executable = @"C:\nope\x.exe" }, env, null, null, @"C:\Users\me");

        Assert.False(resolved.CanLaunch);
        var issue = Assert.Single(resolved.Issues, i => i.IsError);
        Assert.Contains("could not be found", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DirectoryPrecedenceAndFallback()
    {
        var env = new FakeShellEnvironment();
        env.Files.Add(@"C:\Windows\System32\cmd.exe");
        env.Directories.Add(@"C:\work");
        env.Directories.Add(@"C:\profile-dir");
        var profile = new ShellProfile { Id = "cmd", Name = "CMD", Executable = "cmd.exe", StartingDirectory = @"C:\profile-dir" };

        Assert.Equal(@"C:\work", ProfileResolver.Resolve(profile, env, @"C:\work", null, @"C:\home").StartingDirectory);
        Assert.Equal(@"C:\profile-dir", ProfileResolver.Resolve(profile, env, @"C:\gone", null, @"C:\home").StartingDirectory);

        profile.StartingDirectory = null;
        var fallback = ProfileResolver.Resolve(profile, env, @"C:\gone", @"D:\also-gone", @"C:\home");
        Assert.Equal(@"C:\home", fallback.StartingDirectory);
        Assert.True(fallback.CanLaunch);
        Assert.Equal(2, fallback.Issues.Count(i => !i.IsError));
    }

    [Theory]
    [InlineData("pwsh", true)]
    [InlineData("wsl-ubuntu-24.04", true)]
    [InlineData("Has Space", false)]
    [InlineData("UPPER", false)]
    [InlineData("", false)]
    [InlineData("-leading", false)]
    public void ProfileIds_AreValidated(string id, bool valid) => Assert.Equal(valid, ProfileResolver.IsValidId(id));
}

public sealed class ProcessEnvironmentTests
{
    [Fact]
    public void BuildCommandLine_QuotesExecutable()
    {
        Assert.Equal("\"C:\\Program Files\\Git\\bin\\bash.exe\" --login -i", ProcessEnvironment.BuildCommandLine(@"C:\Program Files\Git\bin\bash.exe", "--login -i"));
        Assert.Equal("\"C:\\x.exe\"", ProcessEnvironment.BuildCommandLine(@"C:\x.exe", "  "));
    }

    [Fact]
    public void Merge_OverridesAndRemovesCaseInsensitively()
    {
        var current = new System.Collections.Hashtable { ["Path"] = @"C:\a", ["HOME"] = "x", ["=C:"] = @"C:\" };
        var merged = ProcessEnvironment.Merge(current, new Dictionary<string, string?> { ["PATH"] = @"C:\b", ["home"] = null, ["NEW"] = "1", ["BAD=NAME"] = "x" });

        Assert.Equal(@"C:\b", merged["path"]);
        Assert.False(merged.ContainsKey("HOME"));
        Assert.Equal("1", merged["NEW"]);
        Assert.True(merged.ContainsKey("=C:"));
        Assert.False(merged.ContainsKey("BAD=NAME"));
    }

    [Fact]
    public void EnvironmentBlock_IsDoubleNullTerminated()
    {
        var block = ProcessEnvironment.ToEnvironmentBlock(new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["A"] = "1", ["B"] = "2" });
        Assert.Equal("A=1\0B=2\0\0", block);
    }
}
