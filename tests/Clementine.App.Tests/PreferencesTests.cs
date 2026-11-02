using System.IO;
using System.Windows.Input;
using Clementine.App.Interop;
using Clementine.App.Preferences;
using Clementine.Core;
using Clementine.TestSupport;
using Microsoft.Win32;

namespace Clementine.App.Tests;

/// <summary>
/// Registry integration, run only against a throw-away key under HKCU\Software\ClementineTests that is deleted
/// afterwards. The real Run key and HKCU\Software\Classes are never touched.
/// </summary>
public sealed class ShellRegistrationTests : IDisposable
{
    const string TestRoot = @"Software\ClementineTests";
    readonly string _root = $@"{TestRoot}\Registration-{Guid.NewGuid():N}";
    readonly ShellRegistration _registration;

    public ShellRegistrationTests() =>
        _registration = new ShellRegistration(@"C:\Program Files\Clementine\Clementine.exe", Registry.CurrentUser, _root + @"\Run", _root + @"\Classes");

    [Fact]
    public void Launch_at_login_writes_and_removes_the_run_value()
    {
        Assert.False(_registration.IsLaunchAtLoginEnabled());
        _registration.SetLaunchAtLogin(true);
        Assert.True(_registration.IsLaunchAtLoginEnabled());
        using (var key = Registry.CurrentUser.OpenSubKey(_root + @"\Run"))
            Assert.Equal("\"C:\\Program Files\\Clementine\\Clementine.exe\" --background", key!.GetValue("Clementine"));

        _registration.SetLaunchAtLogin(false);
        Assert.False(_registration.IsLaunchAtLoginEnabled());
        _registration.SetLaunchAtLogin(false); // idempotent
    }

    [Fact]
    public void Context_menu_verb_is_added_for_files_and_folders_and_removed_again()
    {
        Assert.False(_registration.IsContextMenuInstalled());
        _registration.SetContextMenu(true);
        Assert.True(_registration.IsContextMenuInstalled());

        foreach (var target in new[] { @"*\shell\Clementine", @"Directory\shell\Clementine" })
        {
            using var verb = Registry.CurrentUser.OpenSubKey($@"{_root}\Classes\{target}");
            Assert.NotNull(verb);
            Assert.Equal("Convert with Clementine", verb!.GetValue("MUIVerb"));
            using var command = verb.OpenSubKey("command");
            Assert.Equal("\"C:\\Program Files\\Clementine\\Clementine.exe\" --wheel \"%1\"", command!.GetValue(null));
        }

        _registration.SetContextMenu(false);
        Assert.False(_registration.IsContextMenuInstalled());
        using var gone = Registry.CurrentUser.OpenSubKey($@"{_root}\Classes\*\shell\Clementine");
        Assert.Null(gone);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(TestRoot, writable: true);
        if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
        {
            parent.Dispose();
            Registry.CurrentUser.DeleteSubKey(TestRoot, throwOnMissingSubKey: false);
        }
    }
}

public class SendToShortcutTests
{
    [Fact]
    public void Shortcut_is_created_in_the_given_folder_and_removed()
    {
        using var ws = new TestWorkspace("sendto");
        var exe = ws.WriteText(@"app\Clementine.exe", "not really an exe");
        var sendTo = new SendToShortcut(ws.Path("SendTo"), exe);

        Assert.False(sendTo.Exists);
        sendTo.Create();
        Assert.True(sendTo.Exists);
        var (target, arguments) = ShellLink.Read(sendTo.ShortcutPath);
        Assert.Equal(exe, target, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("--wheel", arguments);

        sendTo.Remove();
        Assert.False(File.Exists(sendTo.ShortcutPath));
    }
}

public class KeyTextTests
{
    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.Space, "Ctrl+Alt+Space")]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, Key.D7, "Ctrl+Shift+7")]
    [InlineData(ModifierKeys.Windows | ModifierKeys.Alt, Key.F9, "Alt+Win+F9")]
    [InlineData(ModifierKeys.Control, Key.OemPlus, "Ctrl+Plus")]
    public void Hotkeys_format_and_parse_back(ModifierKeys modifiers, Key key, string text)
    {
        Assert.Equal(text, KeyText.FormatHotkey(modifiers, key));
        Assert.True(KeyText.TryParseHotkey(text, out var parsedModifiers, out var parsedKey));
        Assert.Equal(modifiers, parsedModifiers);
        Assert.Equal(key, parsedKey);
    }

    [Fact]
    public void Bare_keys_and_lone_modifiers_are_not_hotkeys()
    {
        Assert.Null(KeyText.FormatHotkey(ModifierKeys.None, Key.A));
        Assert.Null(KeyText.FormatHotkey(ModifierKeys.Shift, Key.A));
        Assert.Null(KeyText.FormatHotkey(ModifierKeys.Control, Key.LeftCtrl));
        Assert.False(KeyText.TryParseHotkey("Space", out _, out _));
        Assert.False(KeyText.TryParseHotkey("", out _, out _));
    }

    [Fact]
    public void Modifier_combinations_read_naturally()
    {
        Assert.Equal("Shift", KeyText.Describe(TriggerModifiers.Shift));
        Assert.Equal("Ctrl + Shift", KeyText.Describe(TriggerModifiers.Shift | TriggerModifiers.Control));
        Assert.Equal("Ctrl + Alt + Shift + Win", KeyText.Describe(TriggerModifiers.Shift | TriggerModifiers.Control | TriggerModifiers.Alt | TriggerModifiers.Windows));
        Assert.Equal("None", KeyText.Describe(TriggerModifiers.None));
    }
}

public class ModifierConflictTests
{
    [Fact]
    public void Defaults_have_no_conflicts()
    {
        var s = new ClementineSettings();
        Assert.Empty(ModifierConflicts.Find(s.ConvertModifiers, s.ToolsModifiers, s.ActionsModifiers));
    }

    [Fact]
    public void Same_keys_and_empty_combinations_are_reported()
    {
        var problems = ModifierConflicts.Find(TriggerModifiers.Shift, TriggerModifiers.Shift, TriggerModifiers.None);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("Convert and Tools use the same keys (Shift)", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Actions wheel needs at least one key", StringComparison.Ordinal));
    }
}

public class ToolStatusTests
{
    [Theory]
    [InlineData("ffmpeg version 8.1.2-full_build-www.gyan.dev Copyright (c) 2000-2026 the FFmpeg developers", "8.1.2")]
    [InlineData("ffmpeg version N-118448-g43be8d0728-20250209 Copyright", "N-118448-g43be8d0728-20250209")]
    [InlineData("ffmpeg version 7.1 Copyright (c) 2000-2024", "7.1")]
    [InlineData("nonsense", null)]
    [InlineData(null, null)]
    public void Ffmpeg_version_is_read_from_the_banner(string? line, string? expected) =>
        Assert.Equal(expected, ToolStatus.ParseFfmpegVersion(line));

    [Fact]
    public async Task Detects_the_tools_on_this_machine()
    {
        var tools = await ToolStatus.DetectAsync(new ToolLocator(() => new ClementineSettings()));
        Assert.Equal(["ffmpeg", "libreoffice", "7zip", "edge"], tools.Select(t => t.Key));
        var ffmpeg = tools[0];
        Assert.True(ffmpeg.IsInstalled, "FFmpeg is installed on the build machine");
        Assert.False(string.IsNullOrEmpty(ffmpeg.Version));
    }
}

public class LicenceTests
{
    [Theory]
    [InlineData("Magick.NET-Q16-HDRI-AnyCPU", "Apache-2.0")]
    [InlineData("PDFsharp", "MIT")]
    [InlineData("Markdig", "BSD-2-Clause")]
    [InlineData("SharpCompress", "MIT")]
    [InlineData("System.IO.Hashing", "MIT")]
    public void Known_packages_have_licences(string id, string licence) => Assert.Equal(licence, Licences.LicenceFor(id));

    [Fact]
    public void Packages_are_read_from_a_deps_file()
    {
        using var ws = new TestWorkspace("deps");
        var deps = ws.WriteText("app.deps.json", """
            { "libraries": {
                "Markdig/1.5.1": { "type": "package" },
                "Clementine.Core/0.1.0": { "type": "project" },
                "runtime.win-x64.Foo/1.0.0": { "type": "package" }
            } }
            """);
        var packages = Licences.BundledPackages(deps);
        var markdig = Assert.Single(packages);
        Assert.Equal("Markdig", markdig.Name);
        Assert.Equal("1.5.1", markdig.Version);
        Assert.Equal("BSD-2-Clause", markdig.Licence);
    }
}
