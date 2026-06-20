using System.IO.Pipes;
using System.Security.Principal;

namespace DevSpaceManager.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string AppId = "DevSpaceManager";
    private const string ShowSettingsCommand = "show-settings";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private Task? _serverTask;

    private SingleInstanceCoordinator(Mutex mutex, bool isFirstInstance)
    {
        _mutex = mutex;
        IsFirstInstance = isFirstInstance;
    }

    public bool IsFirstInstance { get; }

    private static string UserKey
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            return string.Concat(sid.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
        }
    }

    private static string MutexName => $@"Local\{AppId}_{UserKey}";

    private static string PipeName => $"{AppId}_{UserKey}";

    public static SingleInstanceCoordinator Create()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return new SingleInstanceCoordinator(mutex, createdNew);
    }

    public static async Task SignalExistingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1500, cancellationToken);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(ShowSettingsCommand.AsMemory(), cancellationToken);
        }
        catch
        {
            // If the existing instance is still starting, silently exit instead of spawning duplicates.
        }
    }

    public void Start(Action showSettings)
    {
        if (!IsFirstInstance) return;

        var context = SynchronizationContext.Current;
        _serverTask = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(pipe);
                    var command = await reader.ReadLineAsync(_stop.Token);
                    if (string.Equals(command, ShowSettingsCommand, StringComparison.OrdinalIgnoreCase))
                    {
                        if (context is null)
                        {
                            showSettings();
                        }
                        else
                        {
                            context.Post(_ => showSettings(), null);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.App($"Single instance pipe error: {ex}");
                    await Task.Delay(500, _stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
                }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
            // The mutex may already be released during abnormal shutdown.
        }

        _stop.Dispose();
        _mutex.Dispose();
    }
}
