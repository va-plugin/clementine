using Clementine.Engines.Documents.External;
using Clementine.Engines.Documents.Markup;

namespace Clementine.Engines.Documents.Office;

/// <summary>
/// "Built-in + optional": Word and Excel files convert with Clementine's own converters (HTML, printed to PDF by Edge),
/// and with LibreOffice instead whenever it is installed, for the closest match to the original layout.
/// </summary>
internal static class BuiltInOffice
{
    public const string NeedsAPrinter = "Requires Microsoft Edge or LibreOffice";

    const string FallbackWarning = "LibreOffice couldn't convert this file, so Clementine's built-in converter was used — complex layouts may differ.";

    /// <summary>Available with LibreOffice, or with Edge for the built-in path.</summary>
    public static Availability PdfAvailability(DocumentServices services) =>
        services.LibreOffice.IsInstalled || services.Edge.Availability.IsAvailable ? Availability.Available : Availability.Unavailable(NeedsAPrinter);

    /// <summary>
    /// LibreOffice when installed (falling back to the built-in converter if LibreOffice fails on a file the built-in
    /// converter can read), otherwise <paramref name="builtIn"/>.
    /// </summary>
    public static async Task<OperationResult> PreferLibreOfficeAsync(
        DocumentServices services, OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken,
        Func<Task<OperationResult>> withLibreOffice, Func<Task<OperationResult>> builtIn, bool builtInNeedsEdge = true)
    {
        if (!services.LibreOffice.IsInstalled)
            return await builtIn().ConfigureAwait(false);
        try
        {
            return await withLibreOffice().ConfigureAwait(false);
        }
        catch (OperationException failure) when (CanFallBack(services, failure, builtInNeedsEdge))
        {
            services.Context.Log.Warn($"LibreOffice failed on {request.Input.Name} ({failure.Message}); using the built-in converter.");
            try
            {
                var result = await builtIn().ConfigureAwait(false);
                return result with { Warnings = [FallbackWarning, .. result.Warnings] };
            }
            catch (OperationException)
            {
                // The file is unreadable for both: LibreOffice's explanation is the better one.
                throw failure;
            }
        }
    }

    /// <summary>Timeouts (huge files), password protection and cancellation are reported as they are.</summary>
    static bool CanFallBack(DocumentServices services, OperationException failure, bool needsEdge) =>
        (!needsEdge || services.Edge.Availability.IsAvailable)
        && failure.Message != ProtectedDocuments.Message
        && !failure.Message.StartsWith("LibreOffice took longer", StringComparison.Ordinal);

    /// <summary>Prints Clementine-generated HTML to the request's PDF output with Edge.</summary>
    public static async Task<string> PrintAsync(DocumentServices services, OperationRequest request, string html, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        if (!services.Edge.Availability.IsAvailable)
            throw new OperationException("This conversion needs Microsoft Edge (part of Windows) or LibreOffice, and neither was found.");
        var page = Path.Combine(request.TempDirectory, "document-" + Guid.NewGuid().ToString("N")[..8] + ".html");
        await File.WriteAllTextAsync(page, html, TextFiles.Utf8, cancellationToken).ConfigureAwait(false);
        try
        {
            progress.Report(new JobProgress(0.4, "Printing with Edge…"));
            return await Outputs.FileAsync(request, ".pdf", null,
                pdf => services.Edge.PrintAsync(page, pdf, request.TempDirectory, progress, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(page);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Saves Clementine-generated HTML as the request's .html output (scripts locked out as a second guard).</summary>
    public static Task<string> SaveHtmlAsync(OperationRequest request, string html, CancellationToken cancellationToken)
    {
        var page = HtmlLockdown.ForWebPage(html, null);
        return Outputs.FileAsync(request, ".html", null, path => File.WriteAllTextAsync(path, page, TextFiles.Utf8, cancellationToken));
    }
}
