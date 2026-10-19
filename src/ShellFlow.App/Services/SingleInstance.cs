using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using ShellFlow.Core;
using ShellFlow.Core.Persistence;

namespace ShellFlow.App.Services;

/// <summary>
/// Keeps ShellFlow to one window per user and data folder. The first instance owns a named mutex and listens on a
/// named pipe that only the same Windows user can open (<see cref="PipeOptions.CurrentUserOnly"/>). Later launches —
/// Start menu, Win+R, "Open ShellFlow here" — send their command line over the pipe and exit.
/// </summary>
public sealed partial class SingleInstance : IDisposable
{
    private const int ASFW_ANY = -1;

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>
    /// Returns the primary-instance handle, or null after successfully forwarding <paramref name="args"/> to the
    /// running instance (the caller should then exit). If the running instance does not answer, this process
    /// continues as a standalone window rather than failing to open.
    /// </summary>
    public static SingleInstance? Acquire(AppPaths paths, IReadOnlyList<string> args)
    {
        var key = Key(paths);
        var mutex = new Mutex(initiallyOwned: true, $@"Local\ShellFlow-{key}", out var createdNew);
        var pipeName = $"ShellFlow-{key}";
        if (createdNew)
        {
            return new SingleInstance(mutex, pipeName);
        }

        mutex.Dispose();
        if (TryForward(pipeName, args))
        {
            return null;
        }

        Log.Warn("Another ShellFlow instance is running but did not respond; opening a separate window.");
        return new SingleInstance(new Mutex(false), pipeName + "-" + Environment.ProcessId);
    }

    /// <summary>Listens for forwarded launches; <paramref name="onLaunch"/> runs on a background thread.</summary>
    public void Listen(Action<string[]> onLaunch)
    {
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);

                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    var args = await InstanceMessage.ReadAsync(server, timeout.Token).ConfigureAwait(false);
                    if (args is null)
                    {
                        Log.Warn("Ignored a malformed launch request.");
                        continue;
                    }

                    onLaunch(args);
                }
                catch (OperationCanceledException)
                {
                    if (_stop.IsCancellationRequested)
                    {
                        return;
                    }
                }
                catch (IOException ex)
                {
                    Log.Warn($"Launch pipe error: {ex.Message}");
                    await Task.Delay(200).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log.Error("Could not create the launch pipe; new launches will open separate windows", ex);
                    return;
                }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned (standalone fallback instance).
        }

        _mutex.Dispose();
    }

    private static bool TryForward(string pipeName, IReadOnlyList<string> args)
    {
        // The primary may still be starting up, so retry for a few seconds before giving up.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(500);
                AllowSetForegroundWindow(ASFW_ANY); // Let the running window come to the front.
                client.Write(InstanceMessage.Encode(args));
                client.Flush();
                return true;
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Per user and per data folder, so test instances with SHELLFLOW_DATA_DIR stay independent.</summary>
    private static string Key(AppPaths paths)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sid}|{paths.Root.ToUpperInvariant()}"));
        return Convert.ToHexString(hash, 0, 8);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
