using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using ShellFlow.Terminal.Native;
using static ShellFlow.Terminal.Native.NativeMethods;

namespace ShellFlow.Terminal;

public enum TerminalSessionState
{
    Running,
    Exited,
    Closed,
}

/// <summary>
/// A shell process attached to a Windows pseudo console (ConPTY).
/// Output is decoded from UTF-8 on a dedicated thread and exposed through a bounded channel, so a slow
/// consumer applies backpressure all the way to the shell instead of buffering without limit.
/// </summary>
public sealed class TerminalSession : IAsyncDisposable
{
    private const int ReadBufferSize = 32 * 1024;
    private const int OutputChannelCapacity = 64;
    private static readonly TimeSpan BaselineDelay = TimeSpan.FromSeconds(2.5);

    private readonly SafePseudoConsoleHandle _pseudoConsole;
    private readonly SafeFileHandle _inputWrite;
    private readonly SafeFileHandle _outputRead;
    private readonly SafeProcessHandle _process;
    private readonly SafeJobHandle _job;
    private readonly Channel<string> _output;
    private readonly Channel<string> _input;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _readerThread;
    private readonly Thread _writerThread;
    private readonly RegisteredWaitHandle _exitWait;
    private readonly ProcessWaitHandle _exitWaitHandle;
    private readonly Timer _baselineTimer;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _closing = new();
    private Task? _closeTask;
    private bool _pseudoConsoleClosed;
    private int _processBaseline = 1;

    private TerminalSession(
        SafePseudoConsoleHandle pseudoConsole,
        SafeFileHandle inputWrite,
        SafeFileHandle outputRead,
        SafeProcessHandle process,
        SafeJobHandle job,
        int processId,
        int columns,
        int rows)
    {
        _pseudoConsole = pseudoConsole;
        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _process = process;
        _job = job;
        ProcessId = processId;
        Columns = columns;
        Rows = rows;
        StartedAt = DateTimeOffset.Now;

        _output = Channel.CreateBounded<string>(new BoundedChannelOptions(OutputChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _input = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

        _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = $"ShellFlow PTY reader {processId}" };
        _writerThread = new Thread(WriteLoop) { IsBackground = true, Name = $"ShellFlow PTY writer {processId}" };
        _readerThread.Start();
        _writerThread.Start();

        _exitWaitHandle = new ProcessWaitHandle(process);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitWaitHandle, (_, _) => OnProcessExited(), null, Timeout.Infinite, executeOnlyOnce: true);

        // Some shells (Git Bash, WSL) are launchers that keep a helper process alive. Measure the idle
        // process count shortly after start so "is a command running?" compares against that baseline.
        _baselineTimer = new Timer(_ => CaptureBaseline(), null, BaselineDelay, Timeout.InfiniteTimeSpan);
    }

    public int ProcessId { get; }

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public DateTimeOffset StartedAt { get; }

    public TerminalSessionState State { get; private set; } = TerminalSessionState.Running;

    public int? ExitCode { get; private set; }

    /// <summary>Decoded terminal output. Completes once the pseudo console stops producing output.</summary>
    public ChannelReader<string> Output => _output.Reader;

    /// <summary>Completes with the shell's exit code.</summary>
    public Task<int> Exited => _exited.Task;

    public static TerminalSession Start(TerminalLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureCtrlCIsDelivered();

        if (!Path.IsPathFullyQualified(options.Executable) || !File.Exists(options.Executable))
        {
            throw new TerminalStartException($"The shell executable \"{options.Executable}\" was not found.");
        }

        var workingDirectory = options.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var columns = ClampDimension(options.Columns, 2, 2000);
        var rows = ClampDimension(options.Rows, 1, 1000);

        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        SafePseudoConsoleHandle? pseudoConsole = null;
        SafeJobHandle? job = null;
        SafeProcessHandle? process = null;
        var attributeList = IntPtr.Zero;

        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) ||
                !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
            {
                throw Win32Failure("Could not create the pseudo console pipes");
            }

            var hr = CreatePseudoConsole(new COORD((short)columns, (short)rows), inputRead, outputWrite, 0, out var hpc);
            if (hr != S_OK)
            {
                throw new TerminalStartException($"Could not create the pseudo console (HRESULT 0x{hr:X8}). ConPTY requires Windows 10 version 1809 or later.", hr);
            }

            pseudoConsole = new SafePseudoConsoleHandle(hpc);

            // The pseudo console owns duplicates of these ends now; keeping ours open would prevent EOF.
            inputRead.Dispose();
            outputWrite.Dispose();

            nint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
            {
                throw Win32Failure("Could not initialize the process attribute list");
            }

            if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hpc, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw Win32Failure("Could not attach the pseudo console to the process");
            }

            var environment = ProcessEnvironment.Merge(Environment.GetEnvironmentVariables(), WithTerminalVariables(options.Environment));
            var environmentBlock = ProcessEnvironment.ToEnvironmentBlock(environment);
            var commandLine = ProcessEnvironment.BuildCommandLine(options.Executable, options.Arguments) + '\0';

            var startupInfo = new STARTUPINFOEXW();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEXW>();
            // Without explicit (empty) std handles a child can inherit this process's redirected handles
            // and write around the pseudo console.
            startupInfo.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startupInfo.lpAttributeList = attributeList;

            PROCESS_INFORMATION processInfo;
            bool created;
            unsafe
            {
                fixed (char* commandLinePtr = commandLine)
                fixed (char* environmentPtr = environmentBlock)
                {
                    created = CreateProcess(
                        null,
                        commandLinePtr,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
                        environmentPtr,
                        workingDirectory,
                        ref startupInfo,
                        out processInfo);
                }
            }

            if (!created)
            {
                throw Win32Failure($"Could not start \"{Path.GetFileName(options.Executable)}\"");
            }

            process = new SafeProcessHandle(processInfo.hProcess, ownsHandle: true);

            // The job object is used for accounting (is a command running?) only. It deliberately does not
            // kill on close, so GUI programs launched from the shell survive closing the pane.
            job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid || !AssignProcessToJobObject(job, process))
            {
                Debug.WriteLine($"ShellFlow: job assignment failed ({Marshal.GetLastPInvokeError()}); process accounting disabled.");
            }

            ResumeThread(processInfo.hThread);
            CloseHandle(processInfo.hThread);

            return new TerminalSession(pseudoConsole, inputWrite, outputRead, process, job, processInfo.dwProcessId, columns, rows);
        }
        catch
        {
            if (process is not null && !process.IsInvalid)
            {
                TerminateProcess(process, 1);
            }

            process?.Dispose();
            job?.Dispose();
            pseudoConsole?.Dispose();
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    /// <summary>Queues text to be written to the shell's input. Never blocks the caller.</summary>
    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text) || State != TerminalSessionState.Running)
        {
            return;
        }

        _input.Writer.TryWrite(text);
    }

    public void Resize(int columns, int rows)
    {
        columns = ClampDimension(columns, 2, 2000);
        rows = ClampDimension(rows, 1, 1000);
        lock (_gate)
        {
            if (_pseudoConsoleClosed || (columns == Columns && rows == Rows))
            {
                return;
            }

            var hr = ResizePseudoConsole(_pseudoConsole.DangerousGetHandle(), new COORD((short)columns, (short)rows));
            if (hr == S_OK)
            {
                Columns = columns;
                Rows = rows;
            }
        }
    }

    /// <summary>Number of live processes started from this session (including the shell itself).</summary>
    public int GetActiveProcessCount()
    {
        if (_job.IsInvalid || _job.IsClosed)
        {
            return State == TerminalSessionState.Running ? 1 : 0;
        }

        return QueryInformationJobObject(_job, JobObjectBasicAccountingInformation, out var info, Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
            ? (int)info.ActiveProcesses
            : 0;
    }

    /// <summary>
    /// True when the shell appears to be running a command, i.e. more processes are alive than when the
    /// shell was idle after start. This is a heuristic; it cannot see work done inside the shell process.
    /// </summary>
    public bool HasRunningCommand =>
        State == TerminalSessionState.Running && GetActiveProcessCount() > Volatile.Read(ref _processBaseline);

    /// <summary>
    /// Closes the pseudo console (console clients receive CTRL_CLOSE_EVENT), waits for the shell to exit,
    /// and terminates it if it does not exit within <paramref name="gracePeriod"/>.
    /// </summary>
    public Task CloseAsync(TimeSpan gracePeriod)
    {
        lock (_gate)
        {
            return _closeTask ??= CloseCoreAsync(gracePeriod);
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync(TimeSpan.FromSeconds(2)));

    private async Task CloseCoreAsync(TimeSpan gracePeriod)
    {
        _input.Writer.TryComplete();
        _baselineTimer.Dispose();
        _closing.Cancel();

        await Task.Run(ClosePseudoConsoleOnce).ConfigureAwait(false);

        if (await Task.WhenAny(_exited.Task, Task.Delay(gracePeriod)).ConfigureAwait(false) != _exited.Task)
        {
            TerminateProcess(_process, 1);
            await Task.WhenAny(_exited.Task, Task.Delay(1000)).ConfigureAwait(false);
        }

        // The reader exits once the pseudo console is gone and the pipe reports EOF.
        await Task.Run(() => _readerThread.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

        _exitWait.Unregister(null);
        _exitWaitHandle.Dispose();
        _outputRead.Dispose();
        _inputWrite.Dispose();
        _job.Dispose();
        _process.Dispose();
        _closing.Dispose();
        _output.Writer.TryComplete();
        State = TerminalSessionState.Closed;
        _exited.TrySetResult(ExitCode ?? -1);
    }

    private void ClosePseudoConsoleOnce()
    {
        lock (_gate)
        {
            if (_pseudoConsoleClosed)
            {
                return;
            }

            _pseudoConsoleClosed = true;
        }

        // On older Windows builds ClosePseudoConsole blocks until buffered output is drained; the reader
        // thread keeps draining, and this always runs off the UI thread.
        _pseudoConsole.Dispose();
    }

    private void OnProcessExited()
    {
        var code = GetExitCodeProcess(_process, out var exitCode) ? unchecked((int)exitCode) : -1;
        ExitCode = code;
        if (State == TerminalSessionState.Running)
        {
            State = TerminalSessionState.Exited;
        }

        _input.Writer.TryComplete();

        // Closing the pseudo console lets conhost flush the remaining output and closes the pipe (EOF).
        ThreadPool.QueueUserWorkItem(_ => ClosePseudoConsoleOnce());
        _exited.TrySetResult(code);
    }

    private void CaptureBaseline()
    {
        try
        {
            var count = GetActiveProcessCount();
            if (count > 1)
            {
                Volatile.Write(ref _processBaseline, count);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ReadLoop()
    {
        var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetDecoder();
        var bytes = new byte[ReadBufferSize];
        var chars = new char[ReadBufferSize + 4];

        try
        {
            using var stream = new FileStream(_outputRead, FileAccess.Read, bufferSize: 0, isAsync: false);
            while (true)
            {
                int read;
                try
                {
                    read = stream.Read(bytes, 0, bytes.Length);
                }
                catch (IOException)
                {
                    break; // Broken pipe: the pseudo console has closed.
                }

                if (read <= 0)
                {
                    break;
                }

                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (count == 0)
                {
                    continue;
                }

                if (_closing.IsCancellationRequested)
                {
                    continue; // Keep draining so ClosePseudoConsole can finish, but nobody is listening.
                }

                var chunk = new string(chars, 0, count);
                try
                {
                    // Blocking here is intentional: a full channel means the renderer is behind, so we stop
                    // reading and ConPTY in turn stops the shell from producing more output.
                    _output.Writer.WriteAsync(chunk, _closing.Token).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
                catch (ChannelClosedException)
                {
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }

    private void WriteLoop()
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            using var stream = new FileStream(_inputWrite, FileAccess.Write, bufferSize: 0, isAsync: false);
            var reader = _input.Reader;
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var text))
                {
                    var bytes = encoding.GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                }

                stream.Flush();
            }
        }
        catch (IOException)
        {
            // The shell went away while input was pending.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static IReadOnlyDictionary<string, string?> WithTerminalVariables(IReadOnlyDictionary<string, string?>? overrides)
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["TERM_PROGRAM"] = "ShellFlow",
            ["TERM_PROGRAM_VERSION"] = typeof(TerminalSession).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            ["COLORTERM"] = "truecolor",
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                variables[key] = value;
            }
        }

        return variables;
    }

    private static int ClampDimension(int value, int min, int max) => Math.Clamp(value, min, max);

    private static int _ctrlCRestored;

    /// <summary>
    /// "Ignore Ctrl+C" is a per-process flag inherited by child processes. If ShellFlow was started by a process that
    /// had it set (or in a new process group), every shell would silently ignore Ctrl+C. Clear it once, before the
    /// first shell starts. ShellFlow is a GUI app, so it never receives console Ctrl+C itself.
    /// </summary>
    private static void EnsureCtrlCIsDelivered()
    {
        if (Interlocked.Exchange(ref _ctrlCRestored, 1) == 0 && !SetConsoleCtrlHandler(IntPtr.Zero, false))
        {
            Debug.WriteLine($"ShellFlow: could not restore Ctrl+C processing ({Marshal.GetLastPInvokeError()}).");
        }
    }

    private static TerminalStartException Win32Failure(string message)
    {
        var error = Marshal.GetLastPInvokeError();
        return new TerminalStartException($"{message}: {new Win32Exception(error).Message}", error);
    }
}
