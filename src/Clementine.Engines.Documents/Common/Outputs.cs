namespace Clementine.Engines.Documents.Common;

/// <summary>Writes outputs through <see cref="OutputPlanner"/> so nothing is ever overwritten and failures leave no partial file.</summary>
internal static class Outputs
{
    /// <summary>Reserves "&lt;input&gt;[ (suffix)]&lt;extension&gt;", lets <paramref name="write"/> fill the working path, then commits.</summary>
    public static async Task<string> FileAsync(OperationRequest request, string extension, string? suffix, Func<string, Task> write, InputFile? nameFrom = null)
    {
        using var output = request.Output.BeginFile(nameFrom ?? request.Input, extension, suffix);
        await write(output.WorkingPath).ConfigureAwait(false);
        return output.Commit();
    }

    /// <summary>Synchronous variant of <see cref="FileAsync"/>.</summary>
    public static string File(OperationRequest request, string extension, string? suffix, Action<string> write, InputFile? nameFrom = null)
    {
        using var output = request.Output.BeginFile(nameFrom ?? request.Input, extension, suffix);
        write(output.WorkingPath);
        return output.Commit();
    }

    /// <summary>Copies a finished temp file (from LibreOffice, Edge…) to its final place next to the input.</summary>
    public static string CopyFrom(OperationRequest request, string sourcePath, string extension, string? suffix = null, InputFile? nameFrom = null) =>
        File(request, extension, suffix, working => System.IO.File.Copy(sourcePath, working, overwrite: false), nameFrom);

    /// <summary>Writes a text output (UTF-8, Windows line endings).</summary>
    public static Task<string> TextAsync(OperationRequest request, string text, CancellationToken cancellationToken, string extension = ".txt", string? suffix = null) =>
        FileAsync(request, extension, suffix, path => TextFiles.WriteAsync(path, text, cancellationToken));

    /// <summary>"name 01.png" style names for files inside an output folder, padded to the total count.</summary>
    public static string NumberedName(string baseName, int number, int total, string extension)
    {
        var digits = Math.Max(2, total.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        return OutputPlanner.SanitizeName($"{baseName} {number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0')}") + extension;
    }
}
