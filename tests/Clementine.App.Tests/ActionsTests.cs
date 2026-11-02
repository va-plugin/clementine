using System.IO;
using Clementine.App.Actions;
using Clementine.Core;
using Clementine.TestSupport;

namespace Clementine.App.Tests;

/// <summary>Shared rules every provider must satisfy (see tests/Clementine.TestSupport/ProviderContract.cs).</summary>
public class ProviderContractTests
{
    [Fact]
    public void Actions_provider_satisfies_the_operation_contract()
    {
        using var workspace = new TestWorkspace("contract");
        var problems = ProviderContract.Validate(new ActionsOperationProvider(new FakeShellActions()), workspace.Context);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_action_uses_the_shell_prefix_and_the_actions_wheel()
    {
        using var workspace = new TestWorkspace("contract");
        var operations = new ActionsOperationProvider(new FakeShellActions()).CreateOperations(workspace.Context).ToList();
        Assert.All(operations, op =>
        {
            Assert.StartsWith("action.", op.Info.Id, StringComparison.Ordinal);
            Assert.Equal(OperationCategory.Action, op.Info.Category);
        });
        Assert.Equal(
            ["action.copy-path", "action.copy-path.quoted", "action.copy-path.name", "action.copy-to", "action.move-to", "action.reveal",
             "action.terminal", "action.open-with", "action.shelf", "action.rename", "action.duplicate"],
            operations.Select(o => o.Info.Id));
    }

    [Fact]
    public void Actions_accept_files_folders_and_mixed_selections()
    {
        using var ws = new TestWorkspace("accept");
        var file = InputFile.FromPath(ws.WriteText("notes.weird-extension", "x"));
        var folder = InputFile.FromPath(Directory.CreateDirectory(ws.Path("Folder")).FullName);
        var ops = ws.Operations(new ActionsOperationProvider(new FakeShellActions()));
        Assert.True(ops["action.copy-to"].AcceptsAll([file, folder]));
        Assert.True(ops["action.rename"].AcceptsAll([file]));
        Assert.True(ops["action.open-with"].AcceptsAll([file]));
        Assert.False(ops["action.open-with"].AcceptsAll([folder]));
        Assert.False(ops["action.open-with"].AcceptsAll([file, file]));
    }
}

public class ActionTests
{
    readonly FakeShellActions _shell = new();

    IOperation Op(TestWorkspace ws, string id) => ws.Operation(new ActionsOperationProvider(_shell), id);

    [Fact]
    public async Task Copy_path_puts_paths_on_the_clipboard_and_returns_them_as_text()
    {
        using var ws = new TestWorkspace("copypath");
        var a = ws.WriteText("a b.mp4", "x");
        var c = ws.WriteText("ü.psd", "x");

        var result = await ws.RunAsync(Op(ws, "action.copy-path"), [a, c]);
        Assert.Equal($"{a}{Environment.NewLine}{c}", result.Text);
        Assert.Equal(result.Text, _shell.Clipboard);
        Assert.Equal("Copied 2 paths", result.TextTitle);

        await ws.RunAsync(Op(ws, "action.copy-path.quoted"), [a, c]);
        Assert.Equal($"\"{a}\" \"{c}\"", _shell.Clipboard);

        await ws.RunAsync(Op(ws, "action.copy-path.name"), [a]);
        Assert.Equal("a b.mp4", _shell.Clipboard);
    }

    [Fact]
    public async Task Copy_to_never_overwrites_and_copies_folders_recursively()
    {
        using var ws = new TestWorkspace("copyto");
        var clip = ws.WriteText("src/clip.mov", "new clip");
        ws.WriteText("src/Project/sub/frame 01.png", "frame");
        var destination = ws.Path("dest");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "clip.mov"), "already there");

        var result = await ws.RunAsync(Op(ws, "action.copy-to"), [clip, ws.Path("src/Project")],
            new Dictionary<string, object?> { ["destination"] = destination });

        Assert.Equal([Path.Combine(destination, "clip (2).mov"), Path.Combine(destination, "Project")], result.Outputs);
        Assert.Equal("already there", File.ReadAllText(Path.Combine(destination, "clip.mov")));
        Assert.Equal("new clip", File.ReadAllText(Path.Combine(destination, "clip (2).mov")));
        Assert.Equal("frame", File.ReadAllText(Path.Combine(destination, "Project", "sub", "frame 01.png")));
        Assert.True(File.Exists(clip)); // the original stays
        Assert.Empty(Directory.GetFiles(destination, "*.partial*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Copy_to_without_a_folder_fails_politely()
    {
        using var ws = new TestWorkspace("copyto");
        var ex = await Assert.ThrowsAsync<OperationException>(() => ws.RunAsync(Op(ws, "action.copy-to"), [ws.WriteText("a.txt", "x")]));
        Assert.Equal("Choose a destination folder first.", ex.Message);
    }

    [Fact]
    public async Task Move_to_moves_files_and_folders_without_overwriting()
    {
        using var ws = new TestWorkspace("moveto");
        var clip = ws.WriteText("src/clip.mov", "moving");
        var folder = ws.Path("src/Shots");
        ws.WriteText("src/Shots/a.png", "a");
        var destination = ws.Path("dest");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "clip.mov"), "keep me");

        var result = await ws.RunAsync(Op(ws, "action.move-to"), [clip, folder],
            new Dictionary<string, object?> { ["destination"] = destination });

        Assert.Equal([Path.Combine(destination, "clip (2).mov"), Path.Combine(destination, "Shots")], result.Outputs);
        Assert.False(File.Exists(clip));
        Assert.False(Directory.Exists(folder));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(destination, "clip.mov")));
        Assert.Equal("moving", File.ReadAllText(Path.Combine(destination, "clip (2).mov")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(destination, "Shots", "a.png")));
    }

    [Fact]
    public async Task Move_to_refuses_to_move_a_folder_into_itself()
    {
        using var ws = new TestWorkspace("moveto");
        var folder = Directory.CreateDirectory(ws.Path("Shots")).FullName;
        ws.WriteText("Shots/a.png", "a");
        var ex = await Assert.ThrowsAsync<OperationException>(() => ws.RunAsync(Op(ws, "action.move-to"), [folder],
            new Dictionary<string, object?> { ["destination"] = Path.Combine(folder, "inner") }));
        Assert.Contains("into itself", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder, "a.png")));
    }

    [Fact]
    public async Task Duplicate_makes_a_copy_next_to_the_original()
    {
        using var ws = new TestWorkspace("dup");
        var photo = ws.WriteText("photo.jpg", "pixels");
        var first = await ws.RunAsync(Op(ws, "action.duplicate"), [photo]);
        var second = await ws.RunAsync(Op(ws, "action.duplicate"), [photo]);
        Assert.Equal(ws.Path("photo (copy).jpg"), Assert.Single(first.Outputs));
        Assert.Equal(ws.Path("photo (copy) (2).jpg"), Assert.Single(second.Outputs));
        Assert.Equal("pixels", File.ReadAllText(photo));
    }

    [Fact]
    public async Task Rename_applies_the_pattern_and_keeps_extensions()
    {
        using var ws = new TestWorkspace("rename");
        var files = new[] { ws.WriteText("b.png", "b"), ws.WriteText("a.jpg", "a"), ws.WriteText("c.PNG", "c") };
        var result = await ws.RunAsync(Op(ws, "action.rename"), files,
            new Dictionary<string, object?> { ["pattern"] = "Show {n}", ["start"] = 1.0, ["digits"] = "2" });

        Assert.Equal([ws.Path("Show 01.png"), ws.Path("Show 02.jpg"), ws.Path("Show 03.PNG")], result.Outputs);
        Assert.Equal("b", File.ReadAllText(ws.Path("Show 01.png")));
        Assert.All(files, f => Assert.False(File.Exists(f)));
    }

    [Fact]
    public async Task Rename_handles_swaps_and_never_overwrites_other_files()
    {
        using var ws = new TestWorkspace("rename");
        var one = ws.WriteText("1.txt", "one");
        var two = ws.WriteText("2.txt", "two");
        ws.WriteText("Take 1.txt", "unrelated");

        // "1.txt" → "2.txt" and "2.txt" → "3.txt": a chain through a name that's being freed.
        var result = await ws.RunAsync(Op(ws, "action.rename"), [one, two],
            new Dictionary<string, object?> { ["pattern"] = "{n}", ["start"] = 2.0, ["digits"] = "1" });
        Assert.Equal("one", File.ReadAllText(ws.Path("2.txt")));
        Assert.Equal("two", File.ReadAllText(ws.Path("3.txt")));
        Assert.Equal(2, result.Outputs.Count);

        // "Take {n}" for one file: "Take 1.txt" exists and isn't part of the batch, so it becomes "Take 1 (2).txt".
        var renamed = await ws.RunAsync(Op(ws, "action.rename"), [ws.Path("3.txt")],
            new Dictionary<string, object?> { ["pattern"] = "Take {n}", ["start"] = 1.0, ["digits"] = "1" });
        Assert.Equal(ws.Path("Take 1 (2).txt"), Assert.Single(renamed.Outputs));
        Assert.Equal("unrelated", File.ReadAllText(ws.Path("Take 1.txt")));
    }

    [Fact]
    public void Rename_preview_shows_the_first_names()
    {
        using var ws = new TestWorkspace("rename");
        var inputs = Enumerable.Range(1, 6).Select(i => InputFile.FromPath(ws.WriteText($"clip{i}.mp4", "x"))).ToList();
        var rename = new RenameOperation();
        var preview = rename.Preview(inputs, OptionValues.Defaults(rename.Options));
        Assert.NotNull(preview);
        Assert.Contains("clip1.mp4  →  clip1 1.mp4", preview, StringComparison.Ordinal);
        Assert.Contains("… and 2 more", preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ui_actions_call_the_shell()
    {
        using var ws = new TestWorkspace("ui");
        var file = Path.GetFullPath(ws.WriteText(@"folder\a.mp4", "x"));
        await ws.RunAsync(Op(ws, "action.reveal"), [file]);
        await ws.RunAsync(Op(ws, "action.terminal"), [file]);
        await ws.RunAsync(Op(ws, "action.open-with"), [file]);
        await ws.RunAsync(Op(ws, "action.shelf"), [file]);
        Assert.Equal([file], _shell.Revealed);
        Assert.Equal(Path.GetDirectoryName(file), _shell.Terminal);
        Assert.Equal(file, _shell.OpenedWith);
        Assert.Equal([file], _shell.Pinned);
    }
}

public class RenamePatternTests
{
    [Fact]
    public void Tokens_are_replaced()
    {
        using var ws = new TestWorkspace("pattern");
        var file = InputFile.FromPath(ws.WriteText("Gig Night/Opener.mov", "x"));
        var name = RenamePattern.Apply("{folder} – {name} {n} {date} [{ext}] {unknown}", file, 4, start: 1, digits: 3, modified: new DateTime(2026, 3, 7));
        Assert.Equal("Gig Night – Opener 005 2026-03-07 [mov] {unknown}.mov", name);
    }

    [Fact]
    public void Invalid_file_name_characters_are_replaced()
    {
        using var ws = new TestWorkspace("pattern");
        var file = InputFile.FromPath(ws.WriteText("a.txt", "x"));
        Assert.Equal("a_b_c.txt", RenamePattern.Apply("{name}/b:c", file, 0));
    }

    [Theory]
    [InlineData(9, 1, 1)]
    [InlineData(10, 1, 2)]
    [InlineData(120, 1, 3)]
    [InlineData(5, 98, 3)]
    public void Auto_digits_fit_the_largest_number(int count, int start, int digits) =>
        Assert.Equal(digits, RenamePattern.AutoDigits(count, start));

    [Fact]
    public void Plan_skips_unchanged_names_and_resolves_collisions()
    {
        using var ws = new TestWorkspace("pattern");
        var a = InputFile.FromPath(ws.WriteText("x.txt", "x"));
        var b = InputFile.FromPath(ws.WriteText("y.txt", "y"));
        var plan = RenamePattern.Plan([a, b], "x", 1, 0);
        // "x.txt" keeps its name, so "y.txt" can't take it.
        var single = Assert.Single(plan);
        Assert.Equal(b.Path, single.File.Path);
        Assert.Equal(ws.Path("x (2).txt"), single.NewPath);
    }
}
