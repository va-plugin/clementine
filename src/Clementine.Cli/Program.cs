using System.Text;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.Engines.Documents;
using Clementine.Engines.Files;
using Clementine.Engines.Images;
using Clementine.Engines.Media;

// clementine-cli — headless access to every Clementine operation (tests, scripts, power users).
//
//   clementine-cli list [files...]                          operations (for these files, if given)
//   clementine-cli run <op-id> [--set k=v]... [--out <dir>] <files...>
//   clementine-cli tools                                    external tools Clementine found
//
// Exit codes: 0 success, 1 a job failed, 2 usage error.

Console.OutputEncoding = new UTF8Encoding(false);
return await Cli.MainAsync(args);

static class Cli
{
    static readonly IOperationProvider[] Providers =
    [
        new MediaOperationProvider(),
        new ImageOperationProvider(),
        new DocumentOperationProvider(),
        new FilesOperationProvider(),
    ];

    public static async Task<int> MainAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            return Usage();

        try
        {
            return args[0] switch
            {
                "list" => List(args[1..]),
                "run" => await RunAsync(args[1..]),
                "tools" => Tools(),
                _ => Usage($"Unknown command '{args[0]}'."),
            };
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    static HostServices CreateServices(string? outputDirectory = null)
    {
        // Read the user's settings but never write them back.
        var settings = SettingsStore.InMemory(new SettingsStore(persist: false).Current);
        if (outputDirectory is not null)
        {
            settings.Update(s =>
            {
                s.OutputMode = OutputLocationMode.CustomFolder;
                s.CustomOutputFolder = Path.GetFullPath(outputDirectory);
            });
        }
        return HostServices.Create(Providers, settings, new ConsoleLog());
    }

    static int List(string[] paths)
    {
        var services = CreateServices();
        var files = paths.Select(InputFile.FromPath).ToList();
        var operations = files.Count == 0 ? services.Catalog.All : services.Catalog.ForInputs(files);
        foreach (var category in Enum.GetValues<OperationCategory>())
        {
            var inCategory = operations.Where(o => o.Info.Category == category).OrderBy(o => o.Info.Order).ThenBy(o => o.Info.Id).ToList();
            if (inCategory.Count == 0)
                continue;
            Console.WriteLine($"== {category} ({inCategory.Count})");
            foreach (var op in inCategory)
            {
                var availability = op.CheckAvailability();
                var label = op.Info.Variant is null ? op.Info.Group : $"{op.Info.Group} › {op.Info.Variant}";
                var options = op.Options is null ? "" : $"  [options: {string.Join(", ", op.Options.Fields.Select(f => f.Key))}]";
                var unavailable = availability.IsAvailable ? "" : $"  (unavailable: {availability.Reason})";
                Console.WriteLine($"  {op.Info.Id,-40} {label,-34} {op.InputMode}{options}{unavailable}");
            }
        }
        if (files.Count > 0)
            Console.WriteLine($"Kinds: {string.Join(", ", files.Select(f => $"{f.Name}={f.Kind}"))}");
        return 0;
    }

    static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2)
            return Usage("run needs an operation id and at least one file.");

        var id = args[0];
        var options = new OptionValues();
        string? outputDirectory = null;
        var paths = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--set" when i + 1 < args.Length:
                    var pair = args[++i];
                    var eq = pair.IndexOf('=');
                    if (eq <= 0)
                        return Usage($"--set expects key=value, got '{pair}'.");
                    options[pair[..eq]] = pair[(eq + 1)..];
                    break;
                case "--out" when i + 1 < args.Length:
                    outputDirectory = args[++i];
                    break;
                default:
                    paths.Add(args[i]);
                    break;
            }
        }

        var services = CreateServices(outputDirectory);
        var operation = services.Catalog.Find(id);
        if (operation is null)
            return Usage($"No operation '{id}'. Try: clementine-cli list <file>");

        var files = paths.Select(InputFile.FromPath).ToList();
        if (!operation.AcceptsAll(files))
            return Usage($"'{id}' doesn't accept {string.Join(", ", files.Select(f => f.Name))}.");
        var availability = operation.CheckAvailability();
        if (!availability.IsAvailable)
        {
            Console.Error.WriteLine($"'{id}' is unavailable: {availability.Reason}");
            return 1;
        }

        var dynamicDefaults = await operation.GetDynamicDefaultsAsync(files, CancellationToken.None);
        var merged = OptionValues.Merge(dynamicDefaults, options);

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            services.Jobs.CancelAll();
        };

        var lastPrinted = new Dictionary<Guid, string>();
        services.Jobs.JobChanged += (_, job) =>
        {
            var line = job.State == JobState.Running
                ? $"{job.DisplayName}: {(job.Progress is { } p ? $"{p:P0}" : "…")} {job.Status}"
                : $"{job.DisplayName}: {job.State}";
            lock (lastPrinted)
            {
                if (lastPrinted.TryGetValue(job.Id, out var previous) && previous == line)
                    return;
                lastPrinted[job.Id] = line;
            }
            Console.Error.WriteLine(line);
        };

        var jobs = services.Jobs.Enqueue(operation, files, merged);
        await Task.WhenAll(jobs.Select(j => j.Completion));

        var failed = 0;
        foreach (var job in jobs)
        {
            if (job.State == JobState.Succeeded && job.Result is { } result)
            {
                foreach (var output in result.Outputs)
                    Console.WriteLine(output);
                foreach (var warning in result.Warnings)
                    Console.Error.WriteLine($"warning: {warning}");
                if (result.Text is not null)
                {
                    Console.WriteLine($"--- {result.TextTitle ?? "Result"} ---");
                    Console.WriteLine(result.Text);
                }
            }
            else
            {
                failed++;
                Console.Error.WriteLine($"FAILED {job.DisplayName}: {job.ErrorMessage ?? job.State.ToString()}");
                if (job.ErrorDetails is not null)
                    Console.Error.WriteLine(job.ErrorDetails);
            }
        }
        return failed == 0 ? 0 : 1;
    }

    static int Tools()
    {
        var tools = CreateServices().Tools;
        Console.WriteLine($"ffmpeg      {tools.Ffmpeg ?? "(not found)"}");
        Console.WriteLine($"ffprobe     {tools.Ffprobe ?? "(not found)"}");
        Console.WriteLine($"LibreOffice {tools.LibreOffice ?? "(not found)"}");
        Console.WriteLine($"7-Zip       {tools.SevenZip ?? "(not found)"}");
        Console.WriteLine($"Edge        {tools.Edge ?? "(not found)"}");
        foreach (var app in Enum.GetValues<OfficeApp>())
            Console.WriteLine($"{app,-11} {(tools.IsOfficeInstalled(app) ? "installed" : "(not found)")}");
        return 0;
    }

    static int Usage(string? error = null)
    {
        if (error is not null)
            Console.Error.WriteLine(error);
        Console.Error.WriteLine("""
            usage:
              clementine-cli list [files...]
              clementine-cli run <op-id> [--set key=value]... [--out <dir>] <files...>
              clementine-cli tools
            """);
        return 2;
    }

    sealed class ConsoleLog : ILog
    {
        public void Info(string message)
        {
        }

        public void Warn(string message, Exception? exception = null) =>
            Console.Error.WriteLine($"[warn] {message}{(exception is null ? "" : " " + exception.Message)}");

        public void Error(string message, Exception? exception = null) =>
            Console.Error.WriteLine($"[error] {message}{(exception is null ? "" : Environment.NewLine + exception)}");
    }
}
