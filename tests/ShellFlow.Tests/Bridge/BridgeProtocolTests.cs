using System.Text.Json;
using ShellFlow.Core.Bridge;

namespace ShellFlow.Tests.Bridge;

public sealed class BridgeProtocolTests
{
    private static BridgeMessage Parse(string json)
    {
        Assert.True(BridgeProtocol.TryParse(json, out var message, out var error), error);
        return message;
    }

    private static string Reject(string? json)
    {
        Assert.False(BridgeProtocol.TryParse(json, out var message, out var error));
        Assert.Null(message);
        return error!;
    }

    [Fact]
    public void ValidMessages_Parse()
    {
        Assert.Equal(new ReadyMessage(120, 30), Parse("""{"type":"ready","cols":120,"rows":30}"""));
        Assert.Equal(new InputMessage("ls\r"), Parse("""{"type":"input","data":"ls\r"}"""));
        Assert.Equal(new ResizeMessage(80, 24), Parse("""{"type":"resize","cols":80,"rows":24}"""));
        Assert.Equal(new AckMessage(4096), Parse("""{"type":"ack","chars":4096}"""));
        Assert.Equal(new CommandMessage("pane.splitRight"), Parse("""{"type":"command","id":"pane.splitRight"}"""));
        Assert.Equal(new CopyMessage("hello"), Parse("""{"type":"copy","text":"hello"}"""));
        Assert.Equal(new SearchResultMessage(2, 10), Parse("""{"type":"searchResult","index":2,"count":10}"""));
        Assert.IsType<FocusMessage>(Parse("""{"type":"focus"}"""));
        Assert.Equal(new RendererInfoMessage("webgl"), Parse("""{"type":"renderer","renderer":"webgl"}"""));
    }

    [Theory]
    [InlineData("""{"type":"cwd","path":"C:\\Users\\me"}""", "C:\\Users\\me")]
    [InlineData("""{"type":"cwd","path":"/home/me"}""", "/home/me")]
    [InlineData("""{"type":"cwd","path":"\\\\server\\share"}""", "\\\\server\\share")]
    public void WorkingDirectory_AcceptsKnownPathShapes(string json, string expected)
    {
        Assert.Equal(new WorkingDirectoryMessage(expected), Parse(json));
    }

    [Theory]
    [InlineData("""{"type":"cwd","path":"relative\\dir"}""")]
    [InlineData("""{"type":"cwd","path":"C:\\a\u0007b"}""")]
    [InlineData("""{"type":"cwd","path":"javascript:alert(1)"}""")]
    public void WorkingDirectory_RejectsSuspiciousPaths(string json) => Reject(json);

    [Fact]
    public void Title_StripsControlCharactersAndTruncates()
    {
        var long_ = new string('x', 900);
        var message = (TitleMessage)Parse($$"""{"type":"title","title":"a\u001b]0;evil\u0007b{{long_}}"}""");
        Assert.DoesNotContain('\u001b', message.Title);
        Assert.Equal(BridgeProtocol.MaxTitleLength, message.Title.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"input\"")]
    [InlineData("""{"data":"x"}""")]
    [InlineData("""{"type":42}""")]
    [InlineData("""{"type":"exec","cmd":"calc.exe"}""")]
    [InlineData("""{"type":"navigate","url":"https://example.com"}""")]
    [InlineData("""{"type":"input"}""")]
    [InlineData("""{"type":"input","data":42}""")]
    [InlineData("""{"type":"input","data":""}""")]
    [InlineData("""{"type":"resize","cols":"80","rows":24}""")]
    [InlineData("""{"type":"resize","cols":0,"rows":24}""")]
    [InlineData("""{"type":"resize","cols":80,"rows":99999}""")]
    [InlineData("""{"type":"resize","cols":80.5,"rows":24}""")]
    [InlineData("""{"type":"ack","chars":-1}""")]
    [InlineData("""{"type":"command","id":"../../etc"}""")]
    [InlineData("""{"type":"command","id":"Pane.split"}""")]
    [InlineData("""{"type":"command","id":"x"}""")]
    [InlineData("""{"type":"renderer","renderer":"canvas"}""")]
    [InlineData("""{"type":"input","data":{"nested":{"deep":{"deeper":{"deepest":1}}}}}""")]
    public void InvalidMessages_AreRejected(string? json) => Reject(json);

    [Fact]
    public void OversizedInput_IsRejected()
    {
        var json = JsonSerializer.Serialize(new { type = "input", data = new string('a', BridgeProtocol.MaxInputLength + 1) });
        Assert.Equal("'data' is too long", Reject(json));
    }

    [Fact]
    public void OversizedMessage_IsRejectedBeforeParsing()
    {
        var json = "{\"type\":\"copy\",\"text\":\"" + new string('a', BridgeProtocol.MaxMessageLength) + "\"}";
        Assert.Equal("message too large", Reject(json));
    }

    [Fact]
    public void MaxSizedInput_IsAccepted()
    {
        var data = new string('a', BridgeProtocol.MaxInputLength);
        var json = JsonSerializer.Serialize(new { type = "input", data });
        Assert.Equal(new InputMessage(data), Parse(json));
    }

    [Fact]
    public void UnknownExtraFields_AreIgnored()
    {
        Assert.Equal(new FocusMessage(), Parse("""{"type":"focus","extra":"ignored"}"""));
    }

    [Fact]
    public void RendererMessages_SerializeWithTypeDiscriminator()
    {
        var json = JsonSerializer.Serialize<RendererMessage>(new PasteText("echo hi"), BridgeJsonContext.Default.RendererMessage);
        Assert.Contains("\"type\":\"paste\"", json, StringComparison.Ordinal);
        Assert.Contains("\"data\":\"echo hi\"", json, StringComparison.Ordinal);

        var config = JsonSerializer.Serialize<RendererMessage>(
            new ConfigureRenderer(new RendererOptions
            {
                FontFamily = "Cascadia Mono",
                FontSize = 13,
                LineHeight = 1.2,
                CursorStyle = "bar",
                CursorBlink = true,
                Scrollback = 1000,
                Gpu = true,
                CopyOnSelect = false,
                Theme = TerminalThemes.Dark("#A6B7FF"),
                KeyBindings = [new RendererKeyBinding("KeyP", true, false, true, "app.commandPalette")],
            }),
            BridgeJsonContext.Default.RendererMessage);
        Assert.Contains("\"type\":\"config\"", config, StringComparison.Ordinal);
        Assert.Contains("\"brightGreen\":", config, StringComparison.Ordinal);
        Assert.Contains("\"command\":\"app.commandPalette\"", config, StringComparison.Ordinal);
    }
}
