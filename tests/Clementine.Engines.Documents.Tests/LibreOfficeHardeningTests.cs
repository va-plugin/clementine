using System.Diagnostics;
using Clementine.Core;
using Clementine.Engines.Documents.Tests.Fixtures;
using Clementine.TestSupport;
using static Clementine.Engines.Documents.Tests.Fixtures.DocumentFixtures;

namespace Clementine.Engines.Documents.Tests;

/// <summary>LibreOffice under stress: parallel jobs, a busy profile, kills, timeouts, protected files and odd paths.</summary>
[Collection(ExternalToolsCollection.Name)]
public sealed class LibreOfficeHardeningTests() : DocumentTestBase("lo-hardening")
{
    static string ProfileFragment => new Uri(External.LibreOffice.ProfileDirectory).AbsoluteUri;

    static Task<IReadOnlyList<(int Id, string CommandLine)>> LeftoverSofficeAsync() =>
        ToolProcesses.WaitGoneAsync("soffice.bin", ProfileFragment, TimeSpan.FromSeconds(5));

    [LibreOfficeFact]
    public async Task Two_conversions_at_once_both_succeed()
    {
        var first = Docx(Workspace.Path("first.docx"));
        var second = Docx(Workspace.Path("second.docx"));
        var results = await Task.WhenAll(RunAsync("doc.pdf", first), RunAsync("doc.pdf", second));

        Assert.All(results, r => Assert.Equal(1, PageCount(Single(r))));
        Assert.Empty(await LeftoverSofficeAsync());
    }

    [LibreOfficeFact]
    public async Task A_timed_out_conversion_kills_the_whole_libreoffice_tree()
    {
        var input = Docx(Workspace.Path("slow.docx"));
        var original = External.LibreOffice.Timeout;
        External.LibreOffice.Timeout = TimeSpan.FromSeconds(1.5);
        try
        {
            await Assert.ThrowsAsync<OperationException>(() => RunAsync("doc.pdf", input));
        }
        finally
        {
            External.LibreOffice.Timeout = original;
        }
        Assert.Empty(await LeftoverSofficeAsync());
        Assert.Empty(Directory.GetDirectories(Workspace.Root, "lo-*", SearchOption.AllDirectories));
    }

    [LibreOfficeFact]
    public async Task A_cancelled_conversion_kills_libreoffice_and_removes_its_temp_files()
    {
        var input = Docx(Workspace.Path("cancel.docx"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync("doc.pdf", input, cancellationToken: cancel.Token));

        Assert.Empty(await LeftoverSofficeAsync());
        Assert.Empty(Directory.GetDirectories(Workspace.Root, "lo-*", SearchOption.AllDirectories));
    }

    [LibreOfficeFact]
    public async Task A_libreoffice_already_running_on_our_profile_does_not_break_conversions()
    {
        // Another Clementine (the CLI next to the tray app) or a leftover instance holding the private profile.
        var soffice = Path.Combine(Path.GetDirectoryName(LibreOfficeFactAttribute.Tools.LibreOffice!)!, "soffice.exe");
        Directory.CreateDirectory(External.LibreOffice.ProfileDirectory);
        var start = new ProcessStartInfo(soffice) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck", $"-env:UserInstallation={ProfileFragment}" })
            start.ArgumentList.Add(argument);
        using var squatter = Process.Start(start)!;
        try
        {
            // Wait until the squatter's soffice.bin is up and owns the profile.
            for (var i = 0; i < 100 && ToolProcesses.Find("soffice.bin", ProfileFragment).Count == 0; i++)
                await Task.Delay(100);
            await Task.Delay(3000);

            var input = Docx(Workspace.Path("busy.docx"));
            var run = RunAsync("doc.pdf", input);
            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(90)));
            Assert.True(finished == run, "The conversion hung while another LibreOffice held the profile.");
            Assert.Equal(1, PageCount(Single(await run)));
        }
        finally
        {
            try
            {
                squatter.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            ToolProcesses.KillAll("soffice.bin", ProfileFragment);
            await LeftoverSofficeAsync();
        }
    }

    [LibreOfficeFact]
    public async Task Non_ascii_temp_and_input_folders_work()
    {
        using var workspace = new TestWorkspace("lo Ünï 東京");
        var input = Docx(workspace.Path("Bücher – 東京/Bericht ünï.docx"));
        var result = await workspace.RunAsync(workspace.Operation(Provider, "doc.pdf"), [input]);
        var pdf = Assert.Single(result.Outputs);
        Assert.Equal("Bericht ünï.pdf", Path.GetFileName(pdf));
        Assert.Contains("Quarterly report", Normalize(string.Join(' ', PageTexts(pdf))));
    }

    [LibreOfficeFact]
    public async Task A_fresh_profile_survives_a_killed_first_run()
    {
        var profile = Workspace.Path("fresh profile ü/lo-profile");
        var original = External.LibreOffice.ProfileOverride;
        External.LibreOffice.ProfileOverride = profile;
        try
        {
            var input = Docx(Workspace.Path("first-run.docx"));
            using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync("doc.pdf", input, cancellationToken: cancel.Token));
            Assert.Empty(await ToolProcesses.WaitGoneAsync("soffice.bin", new Uri(profile).AbsoluteUri, TimeSpan.FromSeconds(5)));

            var stopwatch = Stopwatch.StartNew();
            Assert.Equal(1, PageCount(Single(await RunAsync("doc.pdf", input))));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(120), $"First run took {stopwatch.Elapsed}.");
        }
        finally
        {
            External.LibreOffice.ProfileOverride = original;
            ToolProcesses.KillAll("soffice.bin", new Uri(profile).AbsoluteUri);
        }
    }

    [LibreOfficeFact]
    public async Task Password_protected_office_files_get_a_friendly_error_quickly()
    {
        var docx = EncryptedOfficeFixtures.Docx(Workspace.Path("secret.docx"));
        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync("doc.pdf", docx));
        Assert.Contains("password", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"Took {stopwatch.Elapsed}.");

        var xlsx = EncryptedOfficeFixtures.Xlsx(Workspace.Path("secret.xlsx"));
        var sheetError = await Assert.ThrowsAsync<OperationException>(() => RunAsync("sheet.pdf", xlsx));
        Assert.Contains("password", sheetError.Message, StringComparison.OrdinalIgnoreCase);

        var pptx = EncryptedOfficeFixtures.Pptx(Workspace.Path("secret.pptx"));
        var slidesError = await Assert.ThrowsAsync<OperationException>(() => RunAsync("slides.png", pptx));
        Assert.Contains("password", slidesError.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(Directory.GetFiles(Workspace.Root, "*.pdf"));
        Assert.Empty(await LeftoverSofficeAsync());
    }
}
