namespace Clementine.Engines.Media;

/// <summary>Clean-up helpers for partial outputs (a killed ffmpeg may hold its file open for a moment).</summary>
internal static class OutputFiles
{
    /// <summary>Deletes a file or folder, retrying briefly while another process still has it open. Never throws.</summary>
    public static async Task DeleteQuietlyAsync(string path)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                else if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Escapes '%' so FFmpeg's numbered-file patterns (segment, image2) treat a name literally.</summary>
    public static string EscapePattern(string name) => name.Replace("%", "%%", StringComparison.Ordinal);

    /// <summary>
    /// A numbered-file pattern for the segment/image2 muxers, relative to the run's working directory:
    /// <c>.\name %02d.mp4</c>. The <c>.\</c> keeps a name that starts with '-' ("-intro.mp4") from being read as an option.
    /// </summary>
    public static string NumberedPattern(string baseName, string numbering) => @".\" + EscapePattern(baseName) + numbering;

    /// <summary>Text for an FFmpeg concat list: <c>file '…'</c> with single quotes escaped.</summary>
    public static string ConcatEntry(string path) => $"file '{path.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
