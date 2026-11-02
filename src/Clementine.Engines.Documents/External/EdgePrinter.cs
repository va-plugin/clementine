namespace Clementine.Engines.Documents.External;

/// <summary>
/// Prints web pages to PDF with headless Microsoft Edge. Every run uses a throw-away browser profile inside the job's
/// temp folder, so it never touches (or hands the job to) the user's own Edge, and the profile is deleted afterwards
/// even when the print fails or is cancelled.
/// </summary>
/// <remarks>
/// The pages come from untrusted places (saved web pages, downloaded e-books, Markdown with raw HTML). Edge prints a
/// locked-down copy (<see cref="Markup.HtmlLockdown"/>: no scripts, frames or links to network shares, plus a strict
/// Content-Security-Policy), and every network request goes to a dead proxy: a page can't run code, phone home, or pull
/// in remote images, fonts or trackers. Local files (the book's own images, pictures next to the Markdown) still load.
/// (Edge's "--blink-settings=scriptEnabled=false" can't be used: headless --print-to-pdf then silently prints nothing.)
/// </remarks>
internal sealed class EdgePrinter(ToolLocator tools)
{
    public const string MissingReason = "Requires Microsoft Edge";

    static readonly SemaphoreSlim Gate = new(2, 2);

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    public Availability Availability => tools.Edge is null ? Availability.Unavailable(MissingReason) : Availability.Available;

    /// <summary>Switches that keep an untrusted page inert while it is printed.</summary>
    internal static IReadOnlyList<string> LockdownArguments { get; } =
    [
        // Every http/https/ws request goes to a proxy that doesn't exist; "<-loopback>" makes that include 127.0.0.1
        // and localhost, which Chromium otherwise always connects to directly.
        "--proxy-server=http://127.0.0.1:9", "--proxy-bypass-list=<-loopback>",
        // And host names never resolve, in case anything bypasses the proxy (DNS prefetch, WebRTC, QUIC).
        "--host-resolver-rules=MAP * ~NOTFOUND",
        "--dns-prefetch-disable", "--no-pings", "--disable-quic", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
        "--disable-background-networking", "--disable-component-update", "--disable-domain-reliability",
        "--disable-client-side-phishing-detection", "--disable-default-apps", "--disable-sync", "--disable-extensions",
        "--disable-crash-reporter", "--disable-breakpad",
    ];

    /// <summary>Prints <paramref name="htmlPath"/> to <paramref name="pdfPath"/>.</summary>
    public async Task PrintAsync(string htmlPath, string pdfPath, string workDirectory, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var edge = tools.Edge ?? throw new OperationException("This conversion needs Microsoft Edge, which wasn't found.");
        var profile = Path.Combine(workDirectory, "edge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(profile);
        var temporaryPdf = Path.Combine(profile, "print.pdf");

        progress.Report(JobProgress.Indeterminate("Printing with Edge…"));
        try
        {
            // Edge prints a locked-down copy (no scripts, frames or links to network shares); <base> keeps the
            // page's own pictures and stylesheets loading from beside the original.
            var page = Path.Combine(profile, "page.html");
            var html = await Task.Run(() => Markup.HtmlLockdown.ForPrinting(htmlPath), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(page, html, TextFiles.Utf8, cancellationToken).ConfigureAwait(false);

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                List<string> arguments =
                [
                    "--headless=new", "--disable-gpu", "--no-pdf-header-footer", $"--user-data-dir={profile}",
                    "--no-first-run", "--no-default-browser-check", "--mute-audio", "--run-all-compositor-stages-before-draw",
                    .. LockdownArguments,
                    $"--print-to-pdf={temporaryPdf}",
                    FileUris.For(page),
                ];
                var result = await ProcessRunner.RunAsync(edge, arguments,
                    new ProcessRunOptions { Timeout = Timeout, WorkingDirectory = profile }, cancellationToken).ConfigureAwait(false);

                if (!File.Exists(temporaryPdf) || new FileInfo(temporaryPdf).Length == 0)
                    throw new OperationException("Edge couldn't print this page to PDF.", $"exit {result.ExitCode}\n{result.StdOut}\n{result.StdErrTail}".Trim());
                File.Copy(temporaryPdf, pdfPath, overwrite: false);
            }
            catch (OperationException ex) when (ex.Message.Contains("too long", StringComparison.Ordinal))
            {
                throw new OperationException($"Edge took longer than {Timeout.TotalMinutes:0} minutes to print this page and was stopped.", ex.Details, ex);
            }
            finally
            {
                Gate.Release();
            }
        }
        finally
        {
            await DeleteProfileAsync(profile).ConfigureAwait(false);
        }
    }

    /// <summary>Edge's helpers can hold profile files for a moment after it exits (or is killed); retry briefly.</summary>
    static async Task DeleteProfileAsync(string profile)
    {
        for (var attempt = 0; attempt < 25 && Directory.Exists(profile); attempt++)
        {
            try
            {
                Directory.Delete(profile, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }
}
