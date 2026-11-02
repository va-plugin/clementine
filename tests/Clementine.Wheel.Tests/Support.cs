using System.IO;
using System.Runtime.ExceptionServices;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;

namespace Clementine.Wheel.Tests;

/// <summary>Runs WPF code on a fresh STA thread and rethrows its exception.</summary>
internal static class Sta
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(2)))
            throw new TimeoutException("STA test thread did not finish.");
        failure?.Throw();
    }
}

/// <summary>Operations, catalogs and files for wheel tests (never touches the real profile).</summary>
internal static class Fakes
{
    public static OperationInfo Info(string id, string group, string? variant = null, int order = 100,
        OperationCategory category = OperationCategory.Convert, string? icon = "video", string? title = null) => new()
    {
        Id = id,
        Category = category,
        Group = group,
        Variant = variant,
        Title = title ?? (variant is null ? group : $"{group} · {variant}"),
        Description = $"Converts to {group}.",
        IconKey = icon,
        Order = order,
    };

    public static IOperation Op(OperationInfo info, Func<InputFile, bool>? accepts = null, Availability? availability = null) =>
        new DelegateOperation(
            info,
            accepts ?? (_ => true),
            (_, _, _) => Task.FromResult(new OperationResult()),
            availability: availability is { } a ? () => a : null);

    public static OperationCatalog Catalog(params IOperation[] operations) =>
        new(new EngineContext(new ToolLocator(() => new ClementineSettings()), () => new ClementineSettings(), new RecordingLog()),
            [new ListProvider(operations)]);

    public static OperationCatalog SampleCatalog() =>
        new(new EngineContext(new ToolLocator(() => new ClementineSettings()), () => new ClementineSettings(), new RecordingLog()),
            [PreviewSamples.CreateProvider()]);

    /// <summary>A catalog with <paramref name="groups"/> groups for any file; group i has variantCounts[i % n] variants.</summary>
    public static OperationCatalog Groups(int groups, params int[] variantCounts)
    {
        var names = new[]
        {
            "Compress", "Trim", "Crop", "Resize", "Speed", "Frame rate", "Rotate", "Reverse", "Ping-pong", "Split",
            "Snapshot", "Remove audio", "Subtitles", "Metadata", "Info", "Stabilize", "Denoise", "Sharpen", "Watermark",
            "Deinterlace", "Loudness", "Thumbnail", "Colour", "Image sequence", "Extra 25", "Extra 26", "Extra 27",
        };
        var ops = new List<IOperation>();
        for (var g = 0; g < groups; g++)
        {
            var count = variantCounts.Length == 0 ? 1 : variantCounts[g % variantCounts.Length];
            for (var v = 0; v < count; v++)
            {
                ops.Add(Op(Info($"test.g{g}.v{v}", names[g], count > 1 ? $"Variant {v + 1}" : null, order: g * 10 + v,
                    category: OperationCategory.Tool, icon: "trim")));
            }
        }
        return Catalog([.. ops]);
    }

    public static IReadOnlyList<InputFile> Files(TestWorkspace workspace, params string[] names) =>
        names.Select(n => InputFile.FromPath(workspace.WriteText(n, "x"))).ToList();

    public static WheelSession Session(OperationCatalog catalog, IReadOnlyList<InputFile> files, WheelMode mode) =>
        new(WheelModel.Build(catalog, files, mode, new RecordingLog()));

    /// <summary>%TEMP%\ClementineDev\wheel\&lt;name&gt; (the module's scratch area).</summary>
    public static string ScratchDirectory(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ClementineDev", "wheel", name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    sealed class ListProvider(IReadOnlyList<IOperation> operations) : IOperationProvider
    {
        public string Name => "Test";

        public IEnumerable<IOperation> CreateOperations(EngineContext context) => operations;
    }
}
