using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Sockets;

namespace Clementine.Engines.Documents.Tests.Fixtures;

/// <summary>Finds external-tool processes by what their command line mentions (a private profile or temp folder).</summary>
internal static class ToolProcesses
{
    public static IReadOnlyList<(int Id, string CommandLine)> Find(string exeName, string fragment)
    {
        var found = new List<(int, string)>();
        using var searcher = new ManagementObjectSearcher($"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '{exeName}'");
        foreach (var process in searcher.Get().Cast<ManagementObject>())
        {
            using (process)
            {
                var commandLine = process["CommandLine"] as string ?? "";
                if (commandLine.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    found.Add((Convert.ToInt32(process["ProcessId"]), commandLine));
            }
        }
        return found;
    }

    /// <summary>Waits up to <paramref name="grace"/> for every matching process to exit; returns the ones still running.</summary>
    public static async Task<IReadOnlyList<(int Id, string CommandLine)>> WaitGoneAsync(string exeName, string fragment, TimeSpan grace)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var left = Find(exeName, fragment);
            if (left.Count == 0 || stopwatch.Elapsed > grace)
                return left;
            await Task.Delay(200);
        }
    }

    /// <summary>Test cleanup: kills matching processes so a failing test never leaves tools running.</summary>
    public static void KillAll(string exeName, string fragment)
    {
        foreach (var (id, _) in Find(exeName, fragment))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }
}

/// <summary>
/// A loopback TCP listener that accepts and immediately drops every connection, counting them. Pages that try to
/// fetch "http://127.0.0.1:port/…" show up here; a browser with networking blocked never connects.
/// </summary>
internal sealed class ConnectionRecorder : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    int _connections;

    public ConnectionRecorder()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Connections => Volatile.Read(ref _connections);

    async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync();
                Interlocked.Increment(ref _connections);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public void Dispose() => _listener.Stop();
}
