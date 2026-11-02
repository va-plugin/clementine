using System.Windows;
using System.Windows.Threading;
using Clementine.App.Actions;
using Clementine.App.Theme;
using Clementine.Core;

namespace Clementine.App.Tests;

/// <summary>
/// One long-lived STA thread with a WPF dispatcher and an <see cref="Application"/> carrying the shell theme,
/// so UI pieces can be created in tests. Exceptions propagate to the calling test.
/// </summary>
internal static class WpfHost
{
    static readonly Lazy<Dispatcher> Instance = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Run(Action action) => Instance.Value.Invoke(action);

    public static T Run<T>(Func<T> func) => Instance.Value.Invoke(func);

    static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ShellTheme.EnsureLoaded(app);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test host",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (failure is not null)
            throw new InvalidOperationException("Couldn't start the WPF test host.", failure);
        return dispatcher!;
    }
}

/// <summary>Records what the actions asked the desktop to do.</summary>
internal sealed class FakeShellActions : IShellActions
{
    public string? Clipboard { get; private set; }

    public List<string> Revealed { get; } = [];

    public List<string> Pinned { get; } = [];

    public string? Terminal { get; private set; }

    public string? OpenedWith { get; private set; }

    public void SetClipboardText(string text) => Clipboard = text;

    public void Reveal(IReadOnlyList<string> paths) => Revealed.AddRange(paths);

    public void OpenTerminal(string directory) => Terminal = directory;

    public void OpenWith(string file) => OpenedWith = file;

    public void PinToShelf(IReadOnlyList<string> paths) => Pinned.AddRange(paths);
}

/// <summary>Small operations for launcher, toast and job tests.</summary>
internal static class TestOps
{
    public static DelegateOperation Op(
        string id,
        Func<OperationRequest, Task<OperationResult>>? run = null,
        OptionSchema? options = null,
        OperationCategory category = OperationCategory.Convert,
        InputMode mode = InputMode.PerFile,
        Func<Availability>? availability = null,
        string title = "Test op") =>
        new(
            new OperationInfo { Id = id, Category = category, Group = "Test", Title = title, IconKey = "convert" },
            _ => true,
            (request, _, _) => run?.Invoke(request) ?? Task.FromResult(new OperationResult()),
            options,
            mode,
            availability);

    /// <summary>An operation whose dynamic defaults are fixed (e.g. trim end = duration).</summary>
    public sealed class WithDynamicDefaults(OperationInfo info, OptionSchema schema, OptionValues dynamicDefaults) : OperationBase(info)
    {
        public override OptionSchema? Options => schema;

        public override bool Accepts(InputFile file) => true;

        public override Task<OptionValues> GetDynamicDefaultsAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken) =>
            Task.FromResult(new OptionValues(dynamicDefaults));

        public override Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) =>
            Task.FromResult(new OperationResult());
    }

    public sealed class Provider(params IOperation[] operations) : IOperationProvider
    {
        public string Name => "Test";

        public IEnumerable<IOperation> CreateOperations(EngineContext context) => operations;
    }

    public sealed class ThrowingProvider : IOperationProvider
    {
        public string Name => "Broken";

        public IEnumerable<IOperation> CreateOperations(EngineContext context) => throw new InvalidOperationException("engine exploded");
    }
}
