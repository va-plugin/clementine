namespace Clementine.Core;

/// <summary>A file or folder the user handed to Clementine.</summary>
public sealed class InputFile
{
    InputFile(string fullPath, bool isDirectory, long length)
    {
        Path = fullPath;
        IsDirectory = isDirectory;
        Length = length;
        Name = System.IO.Path.GetFileName(fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        DirectoryPath = System.IO.Path.GetDirectoryName(fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar)) ?? fullPath;

        if (isDirectory)
        {
            Extension = "";
            BaseName = Name;
            Kind = FileKind.Folder;
        }
        else
        {
            Extension = FileTypes.ExtensionOf(fullPath);
            BaseName = Extension.Length > 0 && Name.Length > Extension.Length ? Name[..^Extension.Length] : Name;
            Kind = FileTypes.KindOf(Extension);
        }
    }

    /// <summary>Absolute path.</summary>
    public string Path { get; }

    /// <summary>File name including extension ("clip.mp4").</summary>
    public string Name { get; }

    /// <summary>File name without <see cref="Extension"/> ("clip", or "backup" for "backup.tar.gz").</summary>
    public string BaseName { get; }

    /// <summary>Lower-case extension with a leading dot, "" for folders or extension-less files.</summary>
    public string Extension { get; }

    /// <summary>Folder that contains this file (for a folder input: its parent).</summary>
    public string DirectoryPath { get; }

    public FileKind Kind { get; }

    public bool IsDirectory { get; }

    /// <summary>Size in bytes (0 for folders).</summary>
    public long Length { get; }

    public bool HasExtension(IReadOnlySet<string> extensions) => extensions.Contains(Extension);

    public bool HasExtension(params string[] extensions) =>
        extensions.Any(e => string.Equals(e, Extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>Creates an <see cref="InputFile"/> for an existing file or folder.</summary>
    /// <exception cref="FileNotFoundException">Nothing exists at <paramref name="path"/>.</exception>
    public static InputFile FromPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (Directory.Exists(full))
            return new InputFile(full, isDirectory: true, length: 0);
        if (File.Exists(full))
            return new InputFile(full, isDirectory: false, length: new FileInfo(full).Length);
        throw new FileNotFoundException($"'{path}' doesn't exist.", full);
    }

    public override string ToString() => Path;
}
