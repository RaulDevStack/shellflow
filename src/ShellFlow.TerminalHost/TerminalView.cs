using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using ShellFlow.Core.Bridge;
using ShellFlow.Terminal;
using Windows.UI;

namespace ShellFlow.TerminalHost;

/// <summary>
/// Hosts one xterm.js renderer in WebView2 and connects it to a <see cref="TerminalSession"/>.
/// The renderer is treated as untrusted: every message is origin-checked and validated, and the only things it
/// can do are send input to this pane's shell, report its size, and ask for registered commands by id.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Released in Close(), the control's teardown method.")]
public sealed partial class TerminalView : Grid
{
    /// <summary>Unacknowledged output (in characters) allowed in flight before the pump waits for the renderer.</summary>
    private const long HighWaterMark = 512 * 1024;
    private const int MaxBatchChars = 128 * 1024;

    private readonly WebView2 _webView;
    private readonly DispatcherQueue _dispatcher;
    private readonly Queue<RendererMessage> _pendingMessages = new();
    private readonly SemaphoreSlim _ackSignal = new(0, int.MaxValue);
    private CoreWebView2? _core;
    private TaskCompletionSource _rendererReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TerminalSession? _session;
    private CancellationTokenSource? _pumpCancellation;
    private RendererOptions? _options;
    private long _unacknowledged;
    private bool _pageLoaded;
    private bool _closed;
    private int _rejectedMessages;

    public TerminalView()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("TerminalView must be created on a UI thread.");
        _webView = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Children.Add(_webView);
    }

    /// <summary>The renderer measured its grid. Raised on first load and after a renderer restart.</summary>
    public event EventHandler<TerminalSizeEventArgs>? RendererReady;

    public event EventHandler<TerminalSizeEventArgs>? Resized;

    /// <summary>A key chord bound to an application command was pressed inside the terminal.</summary>
    public event EventHandler<string>? CommandRequested;

    public event EventHandler<string>? CopyRequested;

    public event EventHandler<string>? TitleChanged;

    public event EventHandler<string>? WorkingDirectoryChanged;

    public event EventHandler? TerminalFocused;

    public event EventHandler<SearchResultEventArgs>? SearchResultsChanged;

    /// <summary>Input arrived while no running session is attached (used for "press Enter to restart").</summary>
    public event EventHandler<string>? InputWithoutSession;

    /// <summary>The renderer could not be created or crashed beyond recovery.</summary>
    public event EventHandler<string>? RendererFailed;

    /// <summary>The cd picker needs a folder listing.</summary>
    public event EventHandler<DirectoryListRequest>? DirectoryListRequested;

    /// <summary>The user picked a folder in the cd picker.</summary>
    public event EventHandler<DirectoryAcceptMessage>? DirectoryAccepted;

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public bool IsRendererReady => _rendererReady.Task.IsCompletedSuccessfully;

    /// <summary>"webgl" or "dom" once the renderer reports it.</summary>
    public string? RendererKind { get; private set; }

    public TerminalSession? Session => _session;

    public async Task InitializeAsync()
    {
        if (!File.Exists(Path.Combine(TerminalWebEnvironment.AssetsFolder, "index.html")))
        {
            RendererFailed?.Invoke(this, $"The terminal renderer files are missing from {TerminalWebEnvironment.AssetsFolder}. Reinstall ShellFlow.");
            return;
        }

        CoreWebView2Environment environment;
        try
        {
            environment = await TerminalWebEnvironment.GetAsync();
            await _webView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FileNotFoundException or InvalidOperationException)
        {
            RendererFailed?.Invoke(this, $"The terminal renderer (Microsoft Edge WebView2) could not start: {ex.Message}");
            return;
        }

        if (_closed)
        {
            return;
        }

        var core = _webView.CoreWebView2;
        _core = core;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = IsDevToolsAllowed();
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreBrowserAcceleratorKeysEnabled = false; // Ctrl+F, Ctrl+R, F5... belong to the shell or to ShellFlow.
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsWebMessageEnabled = true;
        settings.IsScriptEnabled = true;

        core.SetVirtualHostNameToFolderMapping(TerminalWebEnvironment.HostName, TerminalWebEnvironment.AssetsFolder, CoreWebView2HostResourceAccessKind.DenyCors);

        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += OnProcessFailed;

        core.Navigate(TerminalWebEnvironment.EntryUrl);
    }

    /// <summary>Colour shown before the renderer paints, to avoid a white flash.</summary>
    public void SetBackground(Color color) => _webView.DefaultBackgroundColor = color;

    public void Configure(RendererOptions options)
    {
        _options = options;
        Send(new ConfigureRenderer(options));
    }

    /// <summary>Connects a session: output streams to the renderer, input and resizes go to the session.</summary>
    public void Attach(TerminalSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Detach();
        _session = session;
        Interlocked.Exchange(ref _unacknowledged, 0);
        if (Columns > 0 && Rows > 0)
        {
            session.Resize(Columns, Rows);
        }

        _pumpCancellation = new CancellationTokenSource();
        var token = _pumpCancellation.Token;
        _ = Task.Run(() => PumpAsync(session, token), token);
    }

    public void Detach()
    {
        _pumpCancellation?.Cancel();
        _pumpCancellation?.Dispose();
        _pumpCancellation = null;
        _session = null;
    }

    public void Paste(string text) => Send(new PasteText(text));

    public void Find(string query, bool backwards, bool caseSensitive, bool regex, bool wholeWord, bool incremental) =>
        Send(new FindText(query, backwards, caseSensitive, regex, wholeWord, incremental));

    public void ClearFind() => Send(new ClearFind());

    /// <summary>Renderer-local actions: "clear", "selectAll", "copy", "scrollToBottom", "motion:off|full|system".</summary>
    public void RunAction(string action) => Send(new RendererAction(action));

    public void ShowStatus(string kind, string? text) => Send(new SessionStatus(kind, text));

    public void SendDirectoryListing(DirectoryListing listing) => Send(listing);

    public void SetFolderCompletion(bool enabled) => Send(new CompletionMode(enabled));

    public void FocusTerminal()
    {
        _webView.Focus(FocusState.Programmatic);
        Send(new FocusRenderer());
    }

    /// <summary>Releases the renderer. The attached session is detached but not closed; its owner closes it.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Detach();
        _rendererReady.TrySetCanceled();
        if (_core is not null)
        {
            _core.NavigationStarting -= OnNavigationStarting;
            _core.FrameNavigationStarting -= OnFrameNavigationStarting;
            _core.NavigationCompleted -= OnNavigationCompleted;
            _core.NewWindowRequested -= OnNewWindowRequested;
            _core.PermissionRequested -= OnPermissionRequested;
            _core.DownloadStarting -= OnDownloadStarting;
            _core.WebMessageReceived -= OnWebMessageReceived;
            _core.ProcessFailed -= OnProcessFailed;
            _core = null;
        }

        _webView.Close();
        Children.Clear();
        _ackSignal.Dispose();
    }

    private void Send(RendererMessage message)
    {
        if (_closed)
        {
            return;
        }

        if (!_pageLoaded || _core is null)
        {
            // Config is re-sent on load anyway; keep only the latest of each so the queue stays small.
            if (message is not ConfigureRenderer)
            {
                _pendingMessages.Enqueue(message);
            }

            return;
        }

        _core.PostWebMessageAsJson(JsonSerializer.Serialize(message, BridgeJsonContext.Default.RendererMessage));
    }

    private async Task PumpAsync(TerminalSession session, CancellationToken token)
    {
        var reader = session.Output;
        var batch = new StringBuilder();
        try
        {
            await _rendererReady.Task.WaitAsync(token).ConfigureAwait(false);
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (batch.Length < MaxBatchChars && reader.TryRead(out var chunk))
                {
                    batch.Append(chunk);
                }

                // Flow control: let the renderer catch up before sending more. Waiting here leaves output in the
                // session's bounded channel, which in turn pauses the shell — memory stays bounded.
                while (Interlocked.Read(ref _unacknowledged) > HighWaterMark)
                {
                    await _ackSignal.WaitAsync(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
                }

                await _rendererReady.Task.WaitAsync(token).ConfigureAwait(false);

                var text = batch.ToString();
                batch.Clear();
                Interlocked.Add(ref _unacknowledged, text.Length);

                // Keep at most one post in flight. Each WebView2 message has a fixed IPC cost, so while the UI
                // thread is busy, output keeps accumulating in the channel and the next message carries all of it.
                var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_dispatcher.TryEnqueue(() =>
                    {
                        if (!token.IsCancellationRequested && _core is not null && _pageLoaded)
                        {
                            _core.PostWebMessageAsString(text);
                        }

                        posted.TrySetResult();
                    }))
                {
                    break; // The dispatcher is shutting down.
                }

                await posted.Task.WaitAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // The view was closed while the pump was waiting.
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsTrustedUri(args.Uri))
        {
            args.Cancel = true;
            Debug.WriteLine("ShellFlow: blocked navigation away from the terminal renderer.");
        }
    }

    private void OnFrameNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args) => args.Cancel = true;

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;

    private void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args) => args.State = CoreWebView2PermissionState.Deny;

    private void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args) => args.Cancel = true;

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
        {
            RendererFailed?.Invoke(this, $"The terminal renderer failed to load ({args.WebErrorStatus}).");
            return;
        }

        _pageLoaded = true;
        if (_options is not null)
        {
            Send(new ConfigureRenderer(_options));
        }

        while (_pendingMessages.TryDequeue(out var message))
        {
            Send(message);
        }
    }

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        Debug.WriteLine($"ShellFlow: WebView2 process failed: {args.ProcessFailedKind}");
        _pageLoaded = false;
        if (_rendererReady.Task.IsCompleted)
        {
            _rendererReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)
        {
            // The shell keeps running; reload the renderer and keep streaming once it reports ready again.
            sender.Reload();
        }
        else
        {
            RendererFailed?.Invoke(this, "The terminal renderer stopped unexpectedly. Close this pane and open a new one.");
        }
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsTrustedUri(args.Source))
        {
            Reject("untrusted origin");
            return;
        }

        string json;
        try
        {
            json = args.WebMessageAsJson;
        }
        catch (ArgumentException)
        {
            Reject("unreadable message");
            return;
        }

        if (!BridgeProtocol.TryParse(json, out var message, out var error))
        {
            Reject(error);
            return;
        }

        switch (message)
        {
            case InputMessage input:
                if (_session is { State: TerminalSessionState.Running } session)
                {
                    session.Write(input.Data);
                }
                else
                {
                    InputWithoutSession?.Invoke(this, input.Data);
                }

                break;

            case ReadyMessage ready:
                Columns = ready.Columns;
                Rows = ready.Rows;
                Interlocked.Exchange(ref _unacknowledged, 0);
                _session?.Resize(ready.Columns, ready.Rows);
                _rendererReady.TrySetResult();
                RendererReady?.Invoke(this, new TerminalSizeEventArgs(ready.Columns, ready.Rows));
                break;

            case ResizeMessage resize:
                Columns = resize.Columns;
                Rows = resize.Rows;
                _session?.Resize(resize.Columns, resize.Rows);
                Resized?.Invoke(this, new TerminalSizeEventArgs(resize.Columns, resize.Rows));
                break;

            case AckMessage ack:
                if (Interlocked.Add(ref _unacknowledged, -ack.Characters) < 0)
                {
                    Interlocked.Exchange(ref _unacknowledged, 0);
                }

                _ackSignal.Release();
                break;

            case CommandMessage command:
                CommandRequested?.Invoke(this, command.CommandId);
                break;

            case CopyMessage copy:
                CopyRequested?.Invoke(this, copy.Text);
                break;

            case TitleMessage title:
                TitleChanged?.Invoke(this, title.Title);
                break;

            case WorkingDirectoryMessage cwd:
                WorkingDirectoryChanged?.Invoke(this, cwd.Path);
                break;

            case FocusMessage:
                TerminalFocused?.Invoke(this, EventArgs.Empty);
                break;

            case SearchResultMessage result:
                SearchResultsChanged?.Invoke(this, new SearchResultEventArgs(result.Index, result.Count));
                break;

            case RendererInfoMessage info:
                RendererKind = info.Renderer;
                break;

            case DirectoryListRequest listRequest:
                DirectoryListRequested?.Invoke(this, listRequest);
                break;

            case DirectoryAcceptMessage accept:
                DirectoryAccepted?.Invoke(this, accept);
                break;

            case RendererErrorMessage rendererError:
                Debug.WriteLine($"ShellFlow renderer: {rendererError.Message}");
                break;
        }
    }

    private void Reject(string? reason)
    {
        // Never log message contents: they may contain keystrokes or clipboard data.
        if (Interlocked.Increment(ref _rejectedMessages) <= 20)
        {
            Debug.WriteLine($"ShellFlow: rejected renderer message ({reason}).");
        }
    }

    private static bool IsTrustedUri(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
        parsed.Scheme == Uri.UriSchemeHttps &&
        string.Equals(parsed.Host, TerminalWebEnvironment.HostName, StringComparison.OrdinalIgnoreCase) &&
        parsed.IsDefaultPort;

    private static bool IsDevToolsAllowed()
    {
#if DEBUG
        return true;
#else
        return Environment.GetEnvironmentVariable("SHELLFLOW_DEVTOOLS") == "1";
#endif
    }
}

public sealed class TerminalSizeEventArgs(int columns, int rows) : EventArgs
{
    public int Columns { get; } = columns;

    public int Rows { get; } = rows;
}

public sealed class SearchResultEventArgs(int index, int count) : EventArgs
{
    public int Index { get; } = index;

    public int Count { get; } = count;
}
