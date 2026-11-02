using System.IO;
using System.Text;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;

namespace Clementine.Wheel.Tests;

/// <summary>
/// The wheel against the shipped catalog (all engines + the shell's actions), loaded from the built app. Opt-in
/// (CLEMENTINE_REAL_CATALOG=1) because it depends on another project's build output. Writes a text report and PNGs
/// to %TEMP%\ClementineDev\review-wheel\real.
/// </summary>
public class RealCatalogTests
{
    /// <summary>One file of each kind the user drags most (extension-only: the engines never open them here).</summary>
    static readonly string[][] Selections =
    [
        ["Festival loop.mp4"], ["Visual 02.mov"], ["Poster artwork.psd"], ["Photo.jpg"], ["Logo.png"], ["Kick loop.wav"],
        ["Set.mp3"], ["Rider.pdf"], ["Rider.docx"], ["Budget.xlsx"], ["Deck.pptx"], ["Pack.zip"], ["Lyrics.srt"],
        ["Data.json"], ["Notes.txt"], ["Notes.md"], ["Vector.svg"], ["Clip.gif"],
        ["Festival loop.mp4", "Visual 02.mov"], ["Festival loop.mp4", "Poster artwork.psd"], ["Photo.jpg", "Logo.png"],
    ];

    static string Directory_ => Path.Combine(Path.GetTempPath(), "ClementineDev", "review-wheel", "real");

    [RealCatalogFact]
    public void Real_catalog_fits_the_wheel_and_renders()
    {
        var directory = Directory_;
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.GetFiles(directory))
            File.Delete(old);

        Sta.Run(() =>
        {
            var log = new RecordingLog();
            var services = HostServices.Create([], SettingsStore.InMemory(), log);
            var catalog = RealCatalog.TryCreate(services.Engine, out var reason);
            Assert.True(catalog is not null, reason);

            using var ws = new TestWorkspace("real-catalog");
            var report = new StringBuilder();
            var problems = new List<string>();
            report.AppendLine($"{catalog!.All.Count} operations");
            foreach (var names in Selections)
            {
                var files = Fakes.Files(ws, names);
                foreach (var mode in Enum.GetValues<WheelMode>())
                {
                    var model = WheelModel.Build(catalog, files, mode, log);
                    var session = WheelSession.ForDisplay(model, 1.5);
                    var key = $"{string.Join("+", names)} {mode}";
                    report.AppendLine($"== {key}: {model.Groups.Count} groups, {session.Pages.Count} page(s)");
                    foreach (var group in model.Groups)
                    {
                        var variants = string.Join(" | ", group.Options.Select(o => (o.IsAvailable ? "" : "!") + o.Label));
                        report.AppendLine($"   {group.Name} [{group.IconKey}] ({group.Options.Count}): {variants}");
                        if (IconGlyphs.Resolve(group.IconKey, WheelPalette.Current.IconFont) is null)
                            problems.Add($"{key}: group {group.Name} has no drawable icon ({group.IconKey ?? "null"})");
                    }

                    // Common monitor scales: hinted text is a little wider or narrower at each.
                    foreach (var ppd in new[] { 1.0, 1.25, 1.5, 2.0 })
                    {
                        var shown = WheelSession.ForDisplay(model, ppd);
                        var view = new WheelView { PixelsPerDipOverride = ppd };
                        for (var page = 0; page < shown.Pages.Count; page++)
                        {
                            view.Show(shown, WheelInteraction.Drag);
                            foreach (var cut in view.TruncatedLabels())
                                problems.Add($"{key} page {page + 1} @{ppd}x: truncated '{cut}'");
                            shown.NextPage();
                        }
                    }

                    if (names.Length == 1 && !model.IsEmpty)
                        RenderAll(session, Path.Combine(directory, Slug($"{names[0]}-{mode}")));
                }
            }

            WheelPreviews.Render(catalog, directory, log);
            report.AppendLine();
            report.AppendLine("Problems:");
            problems.ForEach(p => report.AppendLine("  " + p));
            report.AppendLine("Log warnings:");
            foreach (var line in log.Lines.Where(l => l.StartsWith("[WARN]") || l.StartsWith("[ERROR]")))
                report.AppendLine("  " + line);
            File.WriteAllText(Path.Combine(directory, "report.txt"), report.ToString());
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }

    /// <summary>Idle wheel plus one PNG per group with variants (fan open, first variant hovered).</summary>
    static void RenderAll(WheelSession session, string prefix)
    {
        session.ClearHover();
        WheelPreviews.RenderSession(session, WheelInteraction.Drag, null, false, prefix + "-idle.png", scale: 1.5);
        for (var page = 0; page < session.Pages.Count; page++)
        {
            for (var s = 0; s < session.Segments.Count; s++)
            {
                if (!session.Segments[s].HasVariants)
                    continue;
                session.ClearHover(); // a jump from the last fan straight onto this group could otherwise be read as aiming at that fan
                session.MovePointer(session.Layout.SegmentCentroid(s));
                var hit = WheelHit.OnVariant(s, Math.Min(1, session.Segments[s].Options.Count - 1));
                session.MovePointer(session.Layout.VariantCentroid(s, hit.Variant));
                WheelPreviews.RenderSession(session, WheelInteraction.Drag, WheelPreviews.PointerFor(session.Layout, hit), false,
                    $"{prefix}-p{page + 1}-{Slug(session.Segments[s].Label)}.png", scale: 1.5);
            }
            session.ClearHover();
            session.NextPage();
        }
    }

    static string Slug(string text) =>
        new(text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
}
