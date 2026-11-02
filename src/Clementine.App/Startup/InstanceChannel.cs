using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Clementine.App.Interop;
using Clementine.Core;

namespace Clementine.App.Startup;

/// <summary>Arguments forwarded from a second launch, with the directory it was started in (for relative paths).</summary>
public sealed record InstanceMessage(string[] Args, string WorkingDirectory)
{
    public ShellCommand ToCommand() => CommandLine.Parse(Args, WorkingDirectory);
}

/// <summary>
/// Named-pipe channel between Clementine processes of the same user. The primary instance runs a
/// <see cref="Server"/>; later launches call <see cref="TrySend(string, InstanceMessage, TimeSpan)"/> and exit.
/// </summary>
public static class InstanceChannel
{
    const int MaxMessageBytes = 4 * 1024 * 1024;
    const int MaxArguments = 32 * 1024;

    /// <summary>Sends <paramref name="message"/> to the primary instance. Returns false if nobody is listening.</summary>
    public static bool TrySend(string pipeName, InstanceMessage message, TimeSpan timeout) =>
        TrySend(pipeName, message, timeout, out _);

    /// <summary>
    /// Sends <paramref name="message"/> and, before that, passes this launch's right to take the foreground on to the
    /// listening process (<c>AllowSetForegroundWindow</c>): Explorer gives that right to the process it starts for a
    /// context-menu click, and without it the running instance's wheel or window couldn't come to the front.
    /// </summary>
    /// <param name="serverProcessId">The listening process, or 0 when it couldn't be determined.</param>
    public static bool TrySend(string pipeName, InstanceMessage message, TimeSpan timeout, out int serverProcessId)
    {
        serverProcessId = 0;
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue));
            serverProcessId = AllowServerToTakeForeground(client);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            client.WaitForPipeDrain();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    static int AllowServerToTakeForeground(NamedPipeClientStream client)
    {
        try
        {
            if (!NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out var processId) || processId <= 0)
                return 0;
            // Fails harmlessly when this process isn't allowed to set the foreground itself (e.g. started from a script).
            NativeMethods.AllowSetForegroundWindow(processId);
            return processId;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or ObjectDisposedException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Decodes a message, or returns null when it isn't one. Never throws for bad input: the pipe is reachable by any
    /// process of this user, so missing fields, nulls, garbage bytes and huge argument lists must all be harmless.
    /// </summary>
    public static InstanceMessage? Decode(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
            return null;
        try
        {
            var message = JsonSerializer.Deserialize<InstanceMessage>(utf8);
            if (message?.Args is not { } args || args.Length > MaxArguments)
                return null;
            // Null entries would crash the command-line parser; a missing working directory falls back to ours.
            var clean = args.Where(a => a is not null).ToArray();
            var directory = string.IsNullOrWhiteSpace(message.WorkingDirectory) || message.WorkingDirectory.Contains('\0')
                ? Environment.CurrentDirectory
                : message.WorkingDirectory;
            return new InstanceMessage(clean, directory);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Listens for messages until disposed. <see cref="MessageReceived"/> is raised on a thread-pool thread.</summary>
    public sealed class Server : IDisposable
    {
        readonly string _pipeName;
        readonly ILog _log;
        readonly CancellationTokenSource _stop = new();
        readonly List<Task> _listeners = [];
        readonly object _gate = new();
        List<InstanceMessage>? _early = [];
        EventHandler<InstanceMessage>? _received;

        public Server(string pipeName, ILog log, int listeners = 4)
        {
            _pipeName = pipeName;
            _log = log;
            for (var i = 0; i < listeners; i++)
                _listeners.Add(Task.Run(ListenAsync));
        }

        /// <summary>
        /// Raised for every message. Messages that arrive before the first handler is attached (the app is still
        /// loading its engines) are kept and delivered, in order, to that first handler as soon as it subscribes.
        /// </summary>
        public event EventHandler<InstanceMessage>? MessageReceived
        {
            add
            {
                lock (_gate)
                {
                    _received += value;
                    if (_early is { } early && value is not null)
                    {
                        _early = null;
                        foreach (var message in early)
                            Deliver(value, message);
                    }
                }
            }
            remove
            {
                lock (_gate)
                    _received -= value;
            }
        }

        void Publish(InstanceMessage message)
        {
            lock (_gate)
            {
                if (_received is { } handler)
                    Deliver(handler, message);
                else
                    _early?.Add(message);
            }
        }

        void Deliver(EventHandler<InstanceMessage> handler, InstanceMessage message)
        {
            try
            {
                handler(this, message);
            }
            catch (Exception ex)
            {
                _log.Error("Instance pipe: message handler failed", ex);
            }
        }

        async Task ListenAsync()
        {
            var failures = 0;
            while (!_stop.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    failures = 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The name is taken by a pipe we can't join (another program, or one created with a lower instance
                    // limit). Retrying at once would spin a core and flood the log: back off, and log only now and then.
                    failures++;
                    if (failures == 1 || failures % 30 == 0)
                        _log.Warn($"Instance pipe: couldn't listen (attempt {failures}); later launches can't reach this instance", ex);
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, 100 * failures)), _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    continue;
                }

                try
                {
                    await using var server = pipe;
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    var message = await ReadAsync(server, _stop.Token).ConfigureAwait(false);
                    if (message is not null)
                        Publish(message);
                    else
                        _log.Warn("Instance pipe: a message that isn't a Clementine command was ignored");
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    _log.Warn("Instance pipe: a launch connected but didn't finish sending in time");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _log.Warn("Instance pipe: bad message ignored", ex);
                }
                catch (Exception ex)
                {
                    _log.Error("Instance pipe listener failed", ex);
                    try
                    {
                        await Task.Delay(250, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        static async Task<InstanceMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxMessageBytes)
                    throw new InvalidOperationException("Message too large.");
            }
            return Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                Task.WaitAll([.. _listeners], TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
            _stop.Dispose();
        }
    }
}
