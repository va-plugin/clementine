using Clementine.Core;

namespace Clementine.TestSupport;

/// <summary>
/// A throw-away folder plus an engine context wired to it. Settings live in memory and
/// CLEMENTINE_DATA_DIR points into %TEMP%, so tests never touch the real profile.
/// Set CLEMENTINE_KEEP_TEST_FILES=1 to keep the folder for inspection.
/// </summary>
public sealed class TestWorkspace : IDisposable
{
    static TestWorkspace()
    {
        Environment.SetEnvironmentVariable(
            "CLEMENTINE_DATA_DIR", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClementineTests", "data"));
    }

    public TestWorkspace(string? name = null)
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClementineTests", $"{name ?? "ws"}-{Guid.NewGuid():N}"[..Math.Min(48, (name ?? "ws").Length + 13)]);
        Directory.CreateDirectory(Root);
        Settings = SettingsStore.InMemory();
        Context = new EngineContext(new ToolLocator(() => Settings.Current), () => Settings.Current, new RecordingLog());
        Output = new OutputPlanner(() => Settings.Current);
    }

    public string Root { get; }

    public SettingsStore Settings { get; }

    public EngineContext Context { get; }

    public OutputPlanner Output { get; }

    public RecordingLog Log => (RecordingLog)Context.Log;

    /// <summary>Absolute path inside the workspace (sub-folders are created).</summary>
    public string Path(string relative)
    {
        var full = System.IO.Path.Combine(Root, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        return full;
    }

    public string WriteText(string relative, string contents)
    {
        var path = Path(relative);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>Every operation from <paramref name="provider"/>, keyed by id.</summary>
    public IReadOnlyDictionary<string, IOperation> Operations(IOperationProvider provider) =>
        provider.CreateOperations(Context).ToDictionary(o => o.Info.Id, StringComparer.OrdinalIgnoreCase);

    public IOperation Operation(IOperationProvider provider, string id) =>
        Operations(provider).TryGetValue(id, out var op) ? op : throw new KeyNotFoundException($"{provider.Name} has no operation '{id}'.");

    /// <summary>
    /// Runs <paramref name="operation"/> the way the job queue does (defaults merged, private temp folder).
    /// Per-file operations given several inputs run once per input and the results are concatenated.
    /// </summary>
    public async Task<OperationResult> RunAsync(
        IOperation operation,
        IEnumerable<string> inputPaths,
        IReadOnlyDictionary<string, object?>? options = null,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var inputs = inputPaths.Select(InputFile.FromPath).ToList();
        if (!operation.AcceptsAll(inputs))
            throw new InvalidOperationException($"{operation.Info.Id} does not accept {string.Join(", ", inputs.Select(i => i.Name))}.");

        var defaults = OptionValues.Merge(OptionValues.Defaults(operation.Options), await operation.GetDynamicDefaultsAsync(inputs, cancellationToken));
        var merged = OptionValues.Merge(defaults, options);
        var batches = operation.InputMode == InputMode.PerFile ? inputs.Select(i => (IReadOnlyList<InputFile>)[i]).ToList() : [inputs];

        var outputs = new List<string>();
        var warnings = new List<string>();
        OperationResult? last = null;
        foreach (var batch in batches)
        {
            var temp = Path($"_temp/{Guid.NewGuid():N}");
            Directory.CreateDirectory(temp);
            var request = new OperationRequest
            {
                Inputs = batch,
                Options = new OptionValues(merged),
                Output = Output,
                Context = Context,
                TempDirectory = temp,
            };
            last = await operation.RunAsync(request, progress ?? new Progress<JobProgress>(), cancellationToken);
            outputs.AddRange(last.Outputs);
            warnings.AddRange(last.Warnings);
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
        return last! with { Outputs = outputs, Warnings = warnings };
    }

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("CLEMENTINE_KEEP_TEST_FILES") == "1")
            return;
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Log that keeps messages so tests can assert on warnings.</summary>
public sealed class RecordingLog : ILog
{
    readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
                return _lines.ToList();
        }
    }

    public void Info(string message) => Add("INFO", message, null);

    public void Warn(string message, Exception? exception = null) => Add("WARN", message, exception);

    public void Error(string message, Exception? exception = null) => Add("ERROR", message, exception);

    void Add(string level, string message, Exception? exception)
    {
        lock (_lines)
            _lines.Add($"[{level}] {message}{(exception is null ? "" : " " + exception.Message)}");
    }
}
