using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Clementine.Core;

public sealed record ProcessRunOptions
{
    public string? WorkingDirectory { get; init; }

    /// <summary>Called for every stdout line (e.g. ffmpeg "-progress pipe:1" key=value lines).</summary>
    public Action<string>? OnStdOut { get; init; }

    /// <summary>Called for every stderr line.</summary>
    public Action<string>? OnStdErr { get; init; }

    /// <summary>Keep all of stdout in <see cref="ProcessResult.StdOut"/> (turn off for chatty tools).</summary>
    public bool CaptureStdOut { get; init; } = true;

    /// <summary>How much of the end of stderr to keep for error messages.</summary>
    public int StdErrTailChars { get; init; } = 32_000;

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public TimeSpan? Timeout { get; init; }

    /// <summary>Encoding of the tool's output. Defaults to UTF-8 (what ffmpeg/ffprobe emit).</summary>
    public Encoding? OutputEncoding { get; init; }
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErrTail, TimeSpan Elapsed)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs external tools without a console window, with argument lists (no manual quoting), line callbacks,
/// cancellation that kills the whole process tree, and a job object so children die with Clementine.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ProcessRunOptions();
        var encoding = options.OutputEncoding ?? new UTF8Encoding(false);
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };
        if (options.WorkingDirectory is not null)
            startInfo.WorkingDirectory = options.WorkingDirectory;
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (options.Environment is not null)
        {
            foreach (var (key, value) in options.Environment)
                startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new TailBuffer(options.StdErrTailChars);
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutClosed.TrySetResult();
                return;
            }
            if (options.CaptureStdOut)
            {
                lock (stdout)
                    stdout.AppendLine(e.Data);
            }
            SafeInvoke(options.OnStdOut, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrClosed.TrySetResult();
                return;
            }
            stderr.AppendLine(e.Data);
            SafeInvoke(options.OnStdErr, e.Data);
        };

        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
                throw new OperationException($"Couldn't start {Path.GetFileName(fileName)}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new OperationException($"Couldn't start {Path.GetFileName(fileName)}: {ex.Message}", fileName, ex);
        }

        ChildProcessJob.TryAssign(process);
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = options.Timeout is { } limit ? new CancellationTokenSource(limit) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout?.Token ?? CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationException($"{Path.GetFileName(fileName)} took too long and was stopped.", stderr.ToString());
        }

        try
        {
            await Task.WhenAll(stdoutClosed.Task, stderrClosed.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        string output;
        lock (stdout)
            output = stdout.ToString();
        return new ProcessResult(process.ExitCode, output, stderr.ToString(), stopwatch.Elapsed);
    }

    static void SafeInvoke(Action<string>? callback, string line)
    {
        try
        {
            callback?.Invoke(line);
        }
        catch
        {
            // A progress parser bug must never take down the conversion.
        }
    }

    static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                // Let the tool actually exit so its partial output file is no longer locked when the caller cleans up.
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    sealed class TailBuffer(int capacity)
    {
        readonly StringBuilder _text = new();

        public void AppendLine(string line)
        {
            lock (_text)
            {
                _text.AppendLine(line);
                if (_text.Length > capacity * 2)
                    _text.Remove(0, _text.Length - capacity);
            }
        }

        public override string ToString()
        {
            lock (_text)
                return _text.Length > capacity ? _text.ToString(_text.Length - capacity, capacity) : _text.ToString();
        }
    }

    /// <summary>A Win32 job object with KILL_ON_JOB_CLOSE: if Clementine exits or crashes, child tools exit too.</summary>
    static class ChildProcessJob
    {
        static readonly Lazy<IntPtr> Handle = new(Create);

        public static void TryAssign(Process process)
        {
            try
            {
                if (Handle.Value != IntPtr.Zero)
                    AssignProcessToJobObject(Handle.Value, process.Handle);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        static IntPtr Create()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
            };
            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var pointer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, pointer, false);
                SetInformationJobObject(job, JobObjectExtendedLimitInformation, pointer, (uint)length);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
            return job;
        }

        const int JobObjectExtendedLimitInformation = 9;
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll")]
        static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
    }
}
