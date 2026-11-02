using System.IO;
using Clementine.Core;

namespace Clementine.App.Actions;

/// <summary>Where a copied or moved item ended up, plus anything the user should know about it.</summary>
internal sealed record TransferResult(string Path, IReadOnlyList<string> Warnings)
{
    public TransferResult(string path)
        : this(path, [])
    {
    }
}

/// <summary>Copy, move and duplicate helpers that never overwrite (names are reserved through <see cref="OutputPlanner"/>).</summary>
internal static class FileTransfer
{
    const int BufferSize = 1 << 20;
    const int ErrorSharingViolation = 32;
    const int ErrorLockViolation = 33;
    const int ErrorAccessDenied = 5;

    /// <summary>Total bytes of files (and of files inside folders, without following links) for progress reporting.</summary>
    public static long SizeOf(IEnumerable<InputFile> inputs) => inputs.Sum(i => i.IsDirectory ? DirectorySize(i.Path) : i.Length);

    /// <summary>Copies a file or folder into <paramref name="destination"/>; returns the final path.</summary>
    public static async Task<TransferResult> CopyIntoAsync(InputFile input, string destination, OutputPlanner planner, Action<long> copied, CancellationToken cancellationToken)
    {
        if (input.IsDirectory)
        {
            RefuseCopyIntoItself(input, destination);
            using var pending = planner.BeginDirectory(destination, input.Name);
            var skipped = await CopyDirectoryAsync(input.Path, pending.WorkingPath, copied, cancellationToken).ConfigureAwait(false);
            return new TransferResult(pending.Commit(), SkippedLinksWarning(input, skipped));
        }
        else
        {
            using var pending = planner.BeginFile(destination, input.BaseName, input.Extension);
            await CopyFileAsync(input.Path, pending.WorkingPath, copied, cancellationToken).ConfigureAwait(false);
            return new TransferResult(pending.Commit());
        }
    }

    /// <summary>Makes "name (copy).ext" (or "name (copy)" for folders) next to the original; returns the final path.</summary>
    public static async Task<TransferResult> DuplicateAsync(InputFile input, OutputPlanner planner, Action<long> copied, CancellationToken cancellationToken)
    {
        if (input.IsDirectory)
        {
            using var pending = planner.BeginDirectory(input, "copy");
            var skipped = await CopyDirectoryAsync(input.Path, pending.WorkingPath, copied, cancellationToken).ConfigureAwait(false);
            return new TransferResult(pending.Commit(), SkippedLinksWarning(input, skipped));
        }
        else
        {
            using var pending = planner.BeginFile(input, input.Extension, "copy");
            await CopyFileAsync(input.Path, pending.WorkingPath, copied, cancellationToken).ConfigureAwait(false);
            return new TransferResult(pending.Commit());
        }
    }

    /// <summary>Throws the friendly "into itself" error when <paramref name="destination"/> is the folder or inside it.</summary>
    public static void RefuseCopyIntoItself(InputFile folder, string destination)
    {
        if (folder.IsDirectory && IsSameOrInside(destination, folder.Path))
            throw new OperationException($"Can't copy the folder \"{folder.Name}\" into itself. Choose a folder outside it.");
    }

    /// <summary>
    /// Moves a file or folder into <paramref name="destination"/> under a free name; returns the final path. If anything
    /// fails after the item left its original place, it is moved back. Across drives the item is copied first and the
    /// original removed only once the copy is complete; if that removal fails the copy stays and a warning says so.
    /// </summary>
    /// <param name="sameVolume">Tests: decides whether a plain rename is possible (default: same drive root).</param>
    public static async Task<TransferResult> MoveIntoAsync(InputFile input, string destination, OutputPlanner planner, Action<long> moved,
        CancellationToken cancellationToken, Func<string, string, bool>? sameVolume = null)
    {
        sameVolume ??= SameVolume;
        var source = input.Path;
        if (input.IsDirectory && IsSameOrInside(destination, source))
            throw new OperationException($"Can't move the folder \"{input.Name}\" into itself.");

        var pending = input.IsDirectory
            ? planner.BeginDirectory(destination, input.Name)
            : planner.BeginFile(destination, input.BaseName, input.Extension);
        // Set only once the item really is at pending.WorkingPath: before that, a failure must never "restore" anything.
        var leftOriginalPlace = false;
        try
        {
            if (input.IsDirectory)
            {
                Directory.Delete(pending.WorkingPath);
                if (sameVolume(source, pending.WorkingPath))
                {
                    Directory.Move(source, pending.WorkingPath);
                    leftOriginalPlace = true;
                    moved(DirectorySize(pending.WorkingPath));
                }
                else
                {
                    // Across drives: copy everything first, then remove the original only once the copy is complete.
                    Directory.CreateDirectory(pending.WorkingPath);
                    var skipped = await CopyDirectoryAsync(source, pending.WorkingPath, moved, cancellationToken).ConfigureAwait(false);
                    var final = pending.Commit();
                    if (skipped.Count > 0)
                    {
                        // Deleting the original would also delete those links, which weren't copied.
                        return new TransferResult(final, [
                            $"Copied to {final}, but the original wasn't removed: it contains links (junctions or symbolic links) that Clementine doesn't copy, such as \"{skipped[0]}\"."]);
                    }
                    return new TransferResult(final, RemoveOriginal(() => Directory.Delete(source, recursive: true), final, isDirectory: true));
                }
            }
            else if (sameVolume(source, pending.WorkingPath))
            {
                File.Move(source, pending.WorkingPath);
                leftOriginalPlace = true;
                moved(input.Length);
            }
            else
            {
                await CopyFileAsync(source, pending.WorkingPath, moved, cancellationToken).ConfigureAwait(false);
                var final = pending.Commit();
                return new TransferResult(final, RemoveOriginal(() => File.Delete(source), final, isDirectory: false));
            }

            return new TransferResult(pending.Commit());
        }
        catch (Exception ex) when (leftOriginalPlace && ex is IOException or UnauthorizedAccessException or OperationException)
        {
            // Put the item back where it was; never let PendingOutput.Dispose delete the user's data.
            try
            {
                if (input.IsDirectory)
                    Directory.Move(pending.WorkingPath, source);
                else
                    File.Move(pending.WorkingPath, source);
            }
            catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
            {
                throw new OperationException(
                    $"Moving \"{input.Name}\" failed half-way; it is now at \"{pending.WorkingPath}\".", ex.ToString(), ex);
            }
            throw new OperationException($"Couldn't move \"{input.Name}\": {ex.Message}", ex.ToString(), ex);
        }
        catch (Exception ex) when (!leftOriginalPlace)
        {
            pending.Dispose();
            if (ex is IOException or UnauthorizedAccessException)
                throw new OperationException($"Couldn't move \"{input.Name}\": {DescribeMoveFailure(ex, input.IsDirectory)}", ex.ToString(), ex);
            throw;
        }
    }

    /// <summary>The copy is committed; a failure to delete the original is a warning, not a failed move.</summary>
    static IReadOnlyList<string> RemoveOriginal(Action delete, string final, bool isDirectory)
    {
        try
        {
            delete();
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var reason = !IsOpenElsewhere(ex) ? ex.Message
                : isDirectory ? "something in it is open in another program."
                : "it is open in another program.";
            return [$"Copied to {final}, but the original couldn't be removed: {reason}"];
        }
    }

    /// <summary>"it is open in another program…" for sharing violations; the system message otherwise.</summary>
    internal static string DescribeMoveFailure(Exception ex, bool isDirectory)
    {
        if (IsOpenElsewhere(ex))
            return "it is open in another program. Close it there and try again.";
        // Renaming a folder with a file open inside it is refused as "access denied".
        if (isDirectory && (ex is UnauthorizedAccessException || (ex.HResult & 0xFFFF) == ErrorAccessDenied))
            return "something inside it is open in another program, or Windows doesn't allow moving it.";
        return ex.Message;
    }

    static bool IsOpenElsewhere(Exception ex) => ex is IOException && (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    public static async Task CopyFileAsync(string source, string target, Action<long> copied, CancellationToken cancellationToken)
    {
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied(read);
            }
        }
        try
        {
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Copies the contents of <paramref name="source"/> into <paramref name="target"/> without following symbolic links or
    /// junctions (which can point anywhere, or back up the tree); returns the skipped links relative to the source.
    /// The whole tree is listed before anything is copied, so files appearing meanwhile never feed the copy.
    /// </summary>
    static async Task<IReadOnlyList<string>> CopyDirectoryAsync(string source, string target, Action<long> copied, CancellationToken cancellationToken)
    {
        var tree = ListTree(source);
        Directory.CreateDirectory(target);
        foreach (var directory in tree.Directories)
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in tree.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            await CopyFileAsync(file, destination, copied, cancellationToken).ConfigureAwait(false);
        }
        return tree.SkippedLinks.Select(link => Path.GetRelativePath(source, link)).ToList();
    }

    sealed record Tree(List<string> Directories, List<string> Files, List<string> SkippedLinks);

    // Hidden and system files are copied too (EnumerationOptions skips them by default); unreadable folders fail the copy.
    static readonly EnumerationOptions OneLevel = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    static Tree ListTree(string root)
    {
        var tree = new Tree([], [], []);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var entry in folder.EnumerateFileSystemInfos("*", OneLevel))
            {
                if (IsLink(entry))
                {
                    tree.SkippedLinks.Add(entry.FullName);
                    continue;
                }
                if (entry is DirectoryInfo directory)
                {
                    tree.Directories.Add(directory.FullName);
                    pending.Push(directory);
                }
                else
                {
                    tree.Files.Add(entry.FullName);
                }
            }
        }
        tree.SkippedLinks.Sort(StringComparer.OrdinalIgnoreCase);
        return tree;
    }

    /// <summary>
    /// Symbolic links and junctions. Other reparse points — OneDrive/Dropbox online-only placeholders, dedup files —
    /// are ordinary files and folders to the user and are copied (reading them downloads them).
    /// </summary>
    static bool IsLink(FileSystemInfo info)
    {
        if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;
        try
        {
            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // can't tell: don't follow it
        }
    }

    static IReadOnlyList<string> SkippedLinksWarning(InputFile folder, IReadOnlyList<string> skipped)
    {
        if (skipped.Count == 0)
            return [];
        var examples = string.Join(", ", skipped.Take(3).Select(s => $"\"{s}\""));
        var more = skipped.Count > 3 ? $" and {skipped.Count - 3} more" : "";
        return [skipped.Count == 1
            ? $"\"{folder.Name}\" contains a link (junction or symbolic link) that wasn't copied: {examples}."
            : $"\"{folder.Name}\" contains {skipped.Count} links (junctions or symbolic links) that weren't copied: {examples}{more}."];
    }

    static long DirectorySize(string path)
    {
        try
        {
            return ListTree(path).Files.Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="candidate"/> is <paramref name="folder"/> or lies inside it.</summary>
    public static bool IsSameOrInside(string candidate, string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)) + Path.DirectorySeparatorChar;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
