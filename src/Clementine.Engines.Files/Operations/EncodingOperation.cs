using System.Text.RegularExpressions;
using Clementine.Engines.Files.Text;

namespace Clementine.Engines.Files.Operations;

/// <summary>
/// "Fix encoding → UTF-8": re-saves a text file as UTF-8 with its text unchanged. Auto-detects UTF-16/legacy code
/// pages and repairs double-encoded UTF-8; the "Choose encoding…" variant lets the user name the source encoding
/// when detection guesses wrong. XML/HTML encoding declarations are updated so they don't lie afterwards.
/// </summary>
internal sealed partial class EncodingOperation(string id, Func<InputFile, bool> accepts, bool manual, int order, string kind) : FileOperation(
    manual
        ? Infos.Tool(id, "Fix encoding", "Convert to UTF-8 from a chosen encoding…", $"Re-save the {kind} as UTF-8, choosing the original encoding yourself.", "text", null, order + 1, "Choose encoding…")
        : Infos.Tool(id, "Fix encoding", "Convert to UTF-8", $"Detect the {kind}'s encoding (UTF-16, Windows-1252, Cyrillic…) and re-save it as UTF-8.", "text", null, order, "To UTF-8"),
    accepts,
    manual ? Schema : null)
{
    static readonly OptionSchema Schema = new("Original encoding",
    [
        new ChoiceField("encoding", "The file was saved as", EncodingCatalog.Choices, "windows-1252")
        {
            Help = "Pick the encoding the file was written in; the result is always UTF-8.",
        },
    ])
    {
        ConfirmLabel = "Convert",
    };

    protected override OperationResult Run(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var input = request.Input;
        progress.Report(JobProgress.Indeterminate("Reading…"));
        var forced = manual ? EncodingCatalog.Resolve(request.Options.GetString("encoding", "windows-1252")) : null;
        var decoded = TextDecoder.ReadFile(input.Path, forced, repairMojibake: !manual);
        cancellationToken.ThrowIfCancellationRequested();

        if (!manual && decoded.IsUtf8 && !decoded.RepairedMojibake)
            return OperationResult.FromText("Already UTF-8", $"{input.Name} is already UTF-8 — nothing to change.");

        var text = FixDeclaredEncoding(decoded.Text, input.Extension);
        using var output = request.Output.BeginFile(input, input.Extension, "UTF-8");
        TextOutput.WriteUtf8(output.WorkingPath, text, input.Extension);

        var warnings = new List<string>();
        if (decoded.RepairedMojibake)
            warnings.Add("Repaired text that had been double-encoded (\"CafÃ©\" → \"Café\").");
        else if (!manual && !decoded.HadBom && decoded.CodePage is not (1200 or 1201))
            warnings.Add($"Detected {decoded.EncodingName}. If the text looks wrong, use Fix encoding › Choose encoding….");
        return new OperationResult { Outputs = [output.Commit()], Warnings = warnings };
    }

    /// <summary>Rewrites &lt;?xml encoding="…"?&gt; and &lt;meta charset&gt; declarations to UTF-8.</summary>
    internal static string FixDeclaredEncoding(string text, string extension)
    {
        if (extension is ".xml" or ".xhtml" or ".svg" or ".plist" or ".gpx" or ".kml" or ".rss")
            return XmlDeclarationEncoding().Replace(text, "${1}UTF-8${2}", 1);
        if (extension is ".html" or ".htm" or ".xhtml")
            return MetaCharset().Replace(text, "${1}UTF-8", 1);
        return text;
    }

    [GeneratedRegex(@"(^\s*<\?xml[^>]*?\bencoding\s*=\s*[""'])[^""']*([""'])")]
    private static partial Regex XmlDeclarationEncoding();

    [GeneratedRegex(@"(<meta[^>]*?charset\s*=\s*[""']?)[A-Za-z0-9_\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();
}
