using ShellFlow.Core;

namespace ShellFlow.Tests;

public sealed class StartupArgumentsTests
{
    [Fact]
    public void NoArguments_IsEmpty()
    {
        var parsed = StartupArguments.Parse([]);
        Assert.Null(parsed.Directory);
        Assert.Null(parsed.ProfileId);
        Assert.Empty(parsed.Warnings);
    }

    [Theory]
    [InlineData(new[] { "-d", @"C:\src" }, @"C:\src")]
    [InlineData(new[] { "--directory", @"D:\My Projects" }, @"D:\My Projects")]
    [InlineData(new[] { @"C:\src" }, @"C:\src")]
    [InlineData(new[] { "-d", "C:" }, @"C:\")]
    [InlineData(new[] { "-d", "C:\"" }, @"C:\")]
    [InlineData(new[] { "-d", "\"C:\\work\"" }, @"C:\work")]
    public void Directory_IsParsedAndRepaired(string[] args, string expected)
    {
        Assert.Equal(expected, StartupArguments.Parse(args).Directory);
    }

    [Fact]
    public void Profile_IsParsed()
    {
        var parsed = StartupArguments.Parse(["--profile", "cmd", "-d", @"C:\x"]);
        Assert.Equal("cmd", parsed.ProfileId);
        Assert.Equal(@"C:\x", parsed.Directory);
    }

    [Fact]
    public void UnknownOptionsAndMissingValues_AreReportedNotFatal()
    {
        var parsed = StartupArguments.Parse(["--frobnicate", "-d"]);
        Assert.Null(parsed.Directory);
        Assert.Equal(2, parsed.Warnings.Count);
    }
}
