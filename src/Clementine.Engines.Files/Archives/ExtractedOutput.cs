namespace Clementine.Engines.Files.Archives;

/// <summary>
/// Extractions are written into a privately named staging folder next to the archive, then given their final
/// name without double nesting: "Pack.zip" holding a single "Pack/" folder becomes "Pack", a single top-level folder
/// with another name keeps that name, a single file (e.g. "notes.txt.gz") lands next to the archive as "notes.txt",
/// and anything else goes into a folder named after the archive. Never overwrites — clashes get " (2)".
/// </summary>
internal static class ExtractedOutput
{
    /// <summary>Reserves a staging folder whose name can't clash with the final result.</summary>
    public static PendingOutput BeginStaging(OutputPlanner planner, string directory) =>
        planner.BeginDirectory(directory, $"Extracting {Guid.NewGuid():N}"[..19]);

    /// <summary>Moves the staged contents to their final name and returns it; dispose the staging output afterwards.</summary>
    public static string Commit(PendingOutput staging, OutputPlanner planner, string directory, string archiveName)
    {
        var children = new DirectoryInfo(staging.WorkingPath).GetFileSystemInfos();
        if (children.Length == 1 && children[0] is FileInfo file)
        {
            // ExtensionOf lower-cases; keep the name exactly as stored ("Photo.JPG" stays "Photo.JPG").
            var extension = file.Name[^FileTypes.ExtensionOf(file.Name).Length..];
            var stem = file.Name.Length > extension.Length ? file.Name[..^extension.Length] : "";
            if (stem.Length == 0 || stem.EndsWith(' '))
                (stem, extension) = (file.Name, "");
            using var target = planner.BeginFile(directory, stem, extension);
            File.Move(file.FullName, target.WorkingPath);
            return target.Commit();
        }

        var (source, name) = children.Length == 1 && children[0] is DirectoryInfo only
            ? (only.FullName, only.Name)
            : (staging.WorkingPath, archiveName);
        using var folder = planner.BeginDirectory(directory, name);
        Directory.Delete(folder.WorkingPath);
        Directory.Move(source, folder.WorkingPath);
        return folder.Commit();
    }
}
