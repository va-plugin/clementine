namespace Clementine.Core.Hosting;

/// <summary>Which wheel to show.</summary>
public enum WheelMode
{
    Convert,
    Tools,
    Actions,
}

public enum LaunchSource
{
    Wheel,
    MainWindow,
    CommandLine,
    ContextMenu,
    Hotkey,
}

/// <summary>A point on the virtual desktop in physical pixels (what Win32 hooks and GetCursorPos report).</summary>
public readonly record struct ScreenPoint(int X, int Y);

public static class WheelModes
{
    public static OperationCategory ToCategory(this WheelMode mode) => mode switch
    {
        WheelMode.Tools => OperationCategory.Tool,
        WheelMode.Actions => OperationCategory.Action,
        _ => OperationCategory.Convert,
    };
}

/// <summary>Starts operations on behalf of the UI: asks for options when needed, then queues jobs.</summary>
public interface IOperationLauncher
{
    /// <summary>Returns the queued jobs, or an empty list if the user cancelled the options dialog.</summary>
    Task<IReadOnlyList<Job>> LaunchAsync(IOperation operation, IReadOnlyList<InputFile> inputs, LaunchSource source, ScreenPoint? anchor = null);
}

/// <summary>The radial wheel (implemented in Clementine.Wheel).</summary>
public interface IWheelPresenter
{
    /// <summary>Installs the global drag monitor and hotkey according to settings. Call on the UI thread.</summary>
    void Start();

    void Stop();

    /// <summary>Re-reads trigger settings (modifiers, hotkey, enabled).</summary>
    void ApplySettings();

    /// <summary>Opens the wheel in click mode for files that aren't being dragged (hotkey, context menu, Send To).</summary>
    void ShowForFiles(IReadOnlyList<InputFile> files, WheelMode mode, ScreenPoint? at = null);

    /// <summary>Renders PNG snapshots of representative wheel states into <paramref name="outputDirectory"/> for design review.</summary>
    void RenderPreviews(string outputDirectory);

    /// <summary>Why the selection hotkey isn't active (e.g. another app owns it), for Settings to show; null when fine.</summary>
    string? HotkeyProblem => null;
}

/// <summary>Launcher without UI: queues with remembered/default options. Used by the CLI and tests.</summary>
public sealed class QueueLauncher(JobQueue jobs, Func<string, OptionValues?>? optionsFor = null) : IOperationLauncher
{
    public Task<IReadOnlyList<Job>> LaunchAsync(IOperation operation, IReadOnlyList<InputFile> inputs, LaunchSource source, ScreenPoint? anchor = null) =>
        Task.FromResult(jobs.Enqueue(operation, inputs, optionsFor?.Invoke(operation.Info.Id)));
}

/// <summary>The app's service container, built once at startup.</summary>
public sealed class HostServices
{
    public required SettingsStore Settings { get; init; }

    public required ILog Log { get; init; }

    public required ToolLocator Tools { get; init; }

    public required EngineContext Engine { get; init; }

    public required OutputPlanner Output { get; init; }

    public required OperationCatalog Catalog { get; init; }

    public required JobQueue Jobs { get; init; }

    /// <summary>Set by the shell during startup.</summary>
    public IOperationLauncher Launcher { get; set; } = null!;

    /// <summary>Set by the shell during startup (null in headless hosts).</summary>
    public IWheelPresenter? Wheel { get; set; }

    public static HostServices Create(IEnumerable<IOperationProvider> providers, SettingsStore? settings = null, ILog? log = null)
    {
        settings ??= new SettingsStore();
        log ??= new FileLog();
        ClementineSettings Current() => settings.Current;

        var tools = new ToolLocator(Current);
        settings.Changed += (_, _) => tools.Refresh();
        var engine = new EngineContext(tools, Current, log);
        var output = new OutputPlanner(Current);
        var jobs = new JobQueue(engine, output);
        settings.Changed += (_, _) => jobs.RefreshLimits();
        var services = new HostServices
        {
            Settings = settings,
            Log = log,
            Tools = tools,
            Engine = engine,
            Output = output,
            Catalog = new OperationCatalog(engine, providers),
            Jobs = jobs,
        };
        services.Launcher = new QueueLauncher(jobs);
        return services;
    }
}
