using System.Globalization;
using System.IO;
using Clementine.App.Options;
using Clementine.Core;

namespace Clementine.App.Actions;

/// <summary>
/// Marker for operations whose only effect is on screen (reveal, terminal, open with, shelf):
/// their toast appears only if they fail.
/// </summary>
public interface IQuietOperation;

/// <summary>
/// The Actions wheel (Alt+Shift-drag): everyday file actions on any files or folders — copy paths, copy/move to a
/// folder, show in Explorer, open a terminal, open with…, pin to the shelf, rename and duplicate.
/// </summary>
public sealed class ActionsOperationProvider : IOperationProvider
{
    readonly IShellActions _shell;

    public ActionsOperationProvider(IShellActions shell) => _shell = shell;

    public string Name => "Actions";

    public IEnumerable<IOperation> CreateOperations(EngineContext context) =>
    [
        new CopyPathOperation(_shell, CopyPathFormat.FullPath),
        new CopyPathOperation(_shell, CopyPathFormat.Quoted),
        new CopyPathOperation(_shell, CopyPathFormat.NameOnly),
        new CopyToOperation(),
        new MoveToOperation(),
        new RevealOperation(_shell),
        new TerminalOperation(_shell),
        new OpenWithOperation(_shell),
        new PinToShelfOperation(_shell),
        new RenameOperation(),
        new DuplicateOperation(),
    ];
}

/// <summary>Base for actions: accept any existing file or folder.</summary>
public abstract class ActionOperation(OperationInfo info) : OperationBase(info)
{
    public override bool Accepts(InputFile file) => true;

    protected static Action<long> ByteProgress(IProgress<JobProgress> progress, long total, string status)
    {
        long done = 0;
        return bytes =>
        {
            done += bytes;
            progress.Report(new JobProgress(total <= 0 ? null : Math.Min(1, (double)done / total), status));
        };
    }
}

public enum CopyPathFormat
{
    FullPath,
    Quoted,
    NameOnly,
}

/// <summary>Copies paths (one per line), quoted paths (space separated) or names to the clipboard; the text is also the result.</summary>
public sealed class CopyPathOperation : ActionOperation
{
    readonly IShellActions _shell;
    readonly CopyPathFormat _format;

    public CopyPathOperation(IShellActions shell, CopyPathFormat format)
        : base(InfoFor(format))
    {
        _shell = shell;
        _format = format;
    }

    public override InputMode InputMode => InputMode.Combined;

    public override int MinInputs => 1;

    public static string Format(IEnumerable<InputFile> inputs, CopyPathFormat format) => format switch
    {
        CopyPathFormat.Quoted => string.Join(" ", inputs.Select(i => $"\"{i.Path}\"")),
        CopyPathFormat.NameOnly => string.Join(Environment.NewLine, inputs.Select(i => i.Name)),
        _ => string.Join(Environment.NewLine, inputs.Select(i => i.Path)),
    };

    public override Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var text = Format(request.Inputs, _format);
        _shell.SetClipboardText(text);
        var what = _format == CopyPathFormat.NameOnly ? "name" : "path";
        var title = request.Inputs.Count == 1 ? $"Copied the {what}" : $"Copied {request.Inputs.Count} {what}s";
        return Task.FromResult(OperationResult.FromText(title, text));
    }

    static OperationInfo InfoFor(CopyPathFormat format) => format switch
    {
        CopyPathFormat.Quoted => new OperationInfo
        {
            Id = "action.copy-path.quoted",
            Category = OperationCategory.Action,
            Group = "Copy path",
            Variant = "Quoted",
            Title = "Copy path (quoted)",
            Description = "Copies \"quoted\" paths separated by spaces, ready to paste into a terminal.",
            IconKey = "copy",
            Order = 11,
        },
        CopyPathFormat.NameOnly => new OperationInfo
        {
            Id = "action.copy-path.name",
            Category = OperationCategory.Action,
            Group = "Copy path",
            Variant = "Name only",
            Title = "Copy name",
            Description = "Copies just the file names, one per line.",
            IconKey = "copy",
            Order = 12,
        },
        _ => new OperationInfo
        {
            Id = "action.copy-path",
            Category = OperationCategory.Action,
            Group = "Copy path",
            Variant = "Full path",
            Title = "Copy path",
            Description = "Copies the full path of each item to the clipboard, one per line.",
            IconKey = "copy",
            Order = 10,
        },
    };
}

/// <summary>Copies the items into a chosen folder. Existing files are never overwritten ("name (2).ext").</summary>
public sealed class CopyToOperation : ActionOperation
{
    public const string DestinationKey = "destination";

    public CopyToOperation()
        : base(new OperationInfo
        {
            Id = "action.copy-to",
            Category = OperationCategory.Action,
            Group = "Copy to…",
            Title = "Copy to…",
            Description = "Copies the items into a folder you choose. Existing files are never overwritten.",
            IconKey = "copy",
            Order = 20,
        })
    {
    }

    public override InputMode InputMode => InputMode.Combined;

    public override int MinInputs => 1;

    public override OptionSchema Options { get; } = new("Copy to", [new FolderField(DestinationKey, "Destination folder")])
    {
        ConfirmLabel = "Copy",
    };

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var destination = Destination(request);
        // Copying a folder into itself would copy the copy, and so on, until the path is too long.
        foreach (var folder in request.Inputs.Where(i => i.IsDirectory))
            FileTransfer.RefuseCopyIntoItself(folder, destination);
        EnsureExists(destination);
        var report = ByteProgress(progress, FileTransfer.SizeOf(request.Inputs), "Copying…");
        var outputs = new List<string>();
        var warnings = new List<string>();
        foreach (var input in request.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var copied = await FileTransfer.CopyIntoAsync(input, destination, request.Output, report, cancellationToken).ConfigureAwait(false);
                outputs.Add(copied.Path);
                warnings.AddRange(copied.Warnings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new OperationException($"Couldn't copy \"{input.Name}\": {ex.Message}", ex.ToString(), ex);
            }
        }
        AddFallbackWarning(outputs, destination, warnings);
        return new OperationResult { Outputs = outputs, Warnings = warnings };
    }

    /// <summary>The chosen folder as a full path (validated, not yet created).</summary>
    internal static string Destination(OperationRequest request)
    {
        var destination = request.Options.GetStringOrNull(DestinationKey)?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(destination))
            throw new OperationException("Choose a destination folder first.");
        try
        {
            return Path.GetFullPath(destination);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new OperationException($"\"{destination}\" isn't a folder path Windows accepts.", ex.ToString(), ex);
        }
    }

    /// <summary>Creates the destination folder if needed.</summary>
    internal static void EnsureExists(string destination)
    {
        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new OperationException($"Can't create the folder \"{destination}\": {ex.Message}", ex.ToString(), ex);
        }
    }

    internal static void AddFallbackWarning(IEnumerable<string> outputs, string destination, List<string> warnings)
    {
        var elsewhere = outputs.FirstOrDefault(o => !string.Equals(Path.GetDirectoryName(o), Path.TrimEndingDirectorySeparator(destination), StringComparison.OrdinalIgnoreCase));
        if (elsewhere is not null)
            warnings.Add($"\"{destination}\" isn't writable, so the items went to {Path.GetDirectoryName(elsewhere)}.");
    }
}

/// <summary>Moves the items into a chosen folder — the only operation that moves inputs, because the user asked for exactly that.</summary>
public sealed class MoveToOperation : ActionOperation
{
    public MoveToOperation()
        : base(new OperationInfo
        {
            Id = "action.move-to",
            Category = OperationCategory.Action,
            Group = "Move to…",
            Title = "Move to…",
            Description = "Moves the items into a folder you choose. Nothing there is overwritten.",
            IconKey = "move",
            Order = 30,
        })
    {
    }

    public override InputMode InputMode => InputMode.Combined;

    public override int MinInputs => 1;

    public override OptionSchema Options { get; } = new("Move to", [new FolderField(CopyToOperation.DestinationKey, "Destination folder")])
    {
        Description = "The originals move to the new folder.",
        ConfirmLabel = "Move",
    };

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var destination = CopyToOperation.Destination(request);
        foreach (var folder in request.Inputs.Where(i => i.IsDirectory))
        {
            if (FileTransfer.IsSameOrInside(destination, folder.Path))
                throw new OperationException($"Can't move the folder \"{folder.Name}\" into itself.");
        }
        CopyToOperation.EnsureExists(destination);
        var report = ByteProgress(progress, FileTransfer.SizeOf(request.Inputs), "Moving…");
        var outputs = new List<string>();
        var warnings = new List<string>();
        foreach (var input in request.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Path.TrimEndingDirectorySeparator(input.DirectoryPath), Path.TrimEndingDirectorySeparator(destination), StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"\"{input.Name}\" is already in that folder.");
                continue;
            }
            var result = await FileTransfer.MoveIntoAsync(input, destination, request.Output, report, cancellationToken).ConfigureAwait(false);
            outputs.Add(result.Path);
            warnings.AddRange(result.Warnings);
        }
        CopyToOperation.AddFallbackWarning(outputs, destination, warnings);
        return new OperationResult { Outputs = outputs, Warnings = warnings };
    }
}

/// <summary>Opens Explorer with the items selected.</summary>
public sealed class RevealOperation(IShellActions shell) : ActionOperation(new OperationInfo
{
    Id = "action.reveal",
    Category = OperationCategory.Action,
    Group = "Show in folder",
    Title = "Show in Explorer",
    Description = "Opens Explorer with the items selected.",
    IconKey = "folder",
    Order = 40,
}), IQuietOperation
{
    public override InputMode InputMode => InputMode.Combined;

    public override int MinInputs => 1;

    public override Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        shell.Reveal(request.Inputs.Select(i => i.Path).ToList());
        return Task.FromResult(new OperationResult());
    }
}

/// <summary>Opens Windows Terminal (or PowerShell) in the folder (or the folder that contains the file).</summary>
public sealed class TerminalOperation(IShellActions shell) : ActionOperation(new OperationInfo
{
    Id = "action.terminal",
    Category = OperationCategory.Action,
    Group = "Terminal",
    Title = "Open terminal here",
    Description = "Opens Windows Terminal (or PowerShell) in this folder.",
    IconKey = "terminal",
    Order = 50,
}), IQuietOperation
{
    public override InputMode InputMode => InputMode.Combined;

    public override int MinInputs => 1;

    public override Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var first = request.Inputs[0];
        shell.OpenTerminal(first.IsDirectory ? first.Path : first.DirectoryPath);
        return Task.FromResult(new OperationResult());
    }
}

/// <summary>The system "Open with" dialog for one file.</summary>
public sealed class OpenWithOperation(IShellActions shell) : ActionOperation(new OperationInfo
{
    Id = "action.open-with",
    Category = OperationCategory.Action,
    Group = "Open with…",
    Title = "Open with…",
    Description = "Choose which app opens this file.",
    IconKey = "open",
    Order = 60,
}), IQuietOperation
{
    public override bool Accepts(InputFile file) => !file.IsDirectory;

    public override bool AcceptsAll(IReadOnlyList<InputFile> files) => files.Count == 1 && Accepts(files[0]);

    public override Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        shell.OpenWith(request.Input.Path);
        return Task.FromResult(new OperationResult());
    }
}

/// <summary>Adds the items to the floating Shelf window.</summary>
public sealed class PinToShelfOperation(IShellActions shell) : ActionOperation(new OperationInfo
{
    Id = "action.shelf",
    Category = OperationCategory.Action,
    Group = "Shelf",
    Title = "Pin to shelf",
    Description = "Keeps the items on a floating shelf so you can drag them out again later.",
    IconKey = "pin",
    Order = 70,
}), IQuietOperation
{
    public override InputMode InputMode => InputMode.Combined;

    public override int MinInputs => 1;

    public override Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        shell.PinToShelf(request.Inputs.Select(i => i.Path).ToList());
        return Task.FromResult(new OperationResult());
    }
}

/// <summary>Batch rename with a pattern, previewed in the options dialog. Renames in two phases so swaps work.</summary>
public sealed class RenameOperation : ActionOperation, IOptionsPreview
{
    public const string PatternKey = "pattern";
    public const string StartKey = "start";
    public const string DigitsKey = "digits";

    public RenameOperation()
        : base(new OperationInfo
        {
            Id = "action.rename",
            Category = OperationCategory.Action,
            Group = "Rename…",
            Title = "Rename…",
            Description = "Renames the items with a pattern such as \"{name} {n}\", previewed before anything changes.",
            IconKey = "rename",
            Order = 80,
        })
    {
    }

    public override InputMode InputMode => InputMode.Combined;

    public override int MinInputs => 1;

    public override OptionSchema Options { get; } = new("Rename",
    [
        new TextField(PatternKey, "New name", RenamePattern.DefaultPattern)
        {
            Placeholder = "{name} {n}",
            Help = "Tokens: {name} original name · {n} number · {date} modified date · {ext} extension · {folder} folder name. The extension is kept.",
        },
        new NumberField(StartKey, "Start numbering at", 1, 0, 100000),
        new ChoiceField(DigitsKey, "Number width",
            [new OptionChoice("auto", "Auto"), new OptionChoice("1", "1"), new OptionChoice("2", "01"), new OptionChoice("3", "001"), new OptionChoice("4", "0001")],
            "auto"),
    ])
    {
        ConfirmLabel = "Rename",
    };

    public string? Preview(IReadOnlyList<InputFile> inputs, OptionValues values)
    {
        var plan = Plan(inputs, values, exists: null);
        if (plan.Count == 0)
            return "Nothing changes with this pattern.";
        var lines = plan.Take(4).Select(p => $"{p.File.Name}  →  {Path.GetFileName(p.NewPath)}").ToList();
        if (plan.Count > 4)
            lines.Add($"… and {plan.Count - 4} more");
        return string.Join("\n", lines);
    }

    public override Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = Plan(request.Inputs, request.Options, exists: null);
        if (plan.Count == 0)
            return Task.FromResult(new OperationResult { Warnings = ["Nothing to rename: the pattern gives the same names."] });

        // Phase 1: every file to a unique temporary name; phase 2: temporary → final. Roll back on failure.
        var staged = new List<(InputFile File, string Temp, string Final)>();
        var done = new List<(InputFile File, string Final)>();
        try
        {
            foreach (var (file, final) in plan)
            {
                var temp = Path.Combine(file.DirectoryPath, $".{Guid.NewGuid():N}.clementine-rename");
                Move(file.Path, temp, file.IsDirectory);
                staged.Add((file, temp, final));
            }
            for (var i = 0; i < staged.Count; i++)
            {
                var (file, temp, final) = staged[i];
                if (File.Exists(final) || Directory.Exists(final))
                    throw new IOException($"\"{Path.GetFileName(final)}\" already exists.");
                Move(temp, final, file.IsDirectory);
                done.Add((file, final));
                progress.Report(new JobProgress((double)(i + 1) / staged.Count, "Renaming…"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var (file, final) in done)
                TryMove(final, file.Path, file.IsDirectory);
            foreach (var (file, temp, _) in staged.Where(s => done.All(d => d.File != s.File)))
                TryMove(temp, file.Path, file.IsDirectory);
            throw new OperationException($"Couldn't rename: {ex.Message} Nothing was changed.", ex.ToString(), ex);
        }
        return Task.FromResult(OperationResult.FromOutputs(done.Select(d => d.Final).ToArray()));
    }

    static IReadOnlyList<(InputFile File, string NewPath)> Plan(IReadOnlyList<InputFile> inputs, OptionValues values, Func<string, bool>? exists)
    {
        var pattern = values.GetString(PatternKey, RenamePattern.DefaultPattern);
        if (string.IsNullOrWhiteSpace(pattern))
            pattern = "{name}";
        var start = Math.Max(0, values.GetInt(StartKey, 1));
        var digitsText = values.GetString(DigitsKey, "auto");
        var digits = int.TryParse(digitsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
            ? d
            : RenamePattern.AutoDigits(inputs.Count, start);
        if (inputs.Count == 1 && digitsText == "auto")
            digits = 0;
        return RenamePattern.Plan(inputs, pattern, start, digits, exists);
    }

    static void Move(string from, string to, bool directory)
    {
        if (directory)
            Directory.Move(from, to);
        else
            File.Move(from, to, overwrite: false);
    }

    static void TryMove(string from, string to, bool directory)
    {
        try
        {
            Move(from, to, directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Copies each item next to the original as "name (copy).ext".</summary>
public sealed class DuplicateOperation() : ActionOperation(new OperationInfo
{
    Id = "action.duplicate",
    Category = OperationCategory.Action,
    Group = "Duplicate",
    Title = "Duplicate",
    Description = "Makes a copy next to the original, named \"… (copy)\".",
    IconKey = "copy",
    Order = 90,
})
{
    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var report = ByteProgress(progress, FileTransfer.SizeOf(request.Inputs), "Copying…");
        try
        {
            var copy = await FileTransfer.DuplicateAsync(request.Input, request.Output, report, cancellationToken).ConfigureAwait(false);
            return new OperationResult { Outputs = [copy.Path], Warnings = copy.Warnings };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new OperationException($"Couldn't duplicate \"{request.Input.Name}\": {ex.Message}", ex.ToString(), ex);
        }
    }
}
