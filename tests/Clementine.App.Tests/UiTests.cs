using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clementine.App.Branding;
using Clementine.App.Options;
using Clementine.App.Shelf;
using Clementine.App.Theme;
using Clementine.App.Toasts;
using Clementine.Core;
using Clementine.TestSupport;

namespace Clementine.App.Tests;

public class IconTests
{
    [Fact]
    public void Every_size_renders_an_opaque_fruit_on_a_transparent_corner()
    {
        WpfHost.Run(() =>
        {
            foreach (var size in IconRenderer.IcoSizes)
            {
                var bitmap = IconRenderer.Render(size);
                Assert.Equal(size, bitmap.PixelWidth);
                Assert.Equal(size, bitmap.PixelHeight);
                var pixels = new byte[size * size * 4];
                bitmap.CopyPixels(pixels, size * 4, 0);
                Assert.Equal(0, pixels[3]); // top-left corner is transparent
                var centre = ((size * 6 / 10) * size + size / 2) * 4;
                Assert.Equal(255, pixels[centre + 3]); // fruit body is opaque…
                Assert.True(pixels[centre + 2] > 200 && pixels[centre] < 120, $"{size}px: centre should be orange"); // …and orange (BGRA)
            }
        });
    }

    [Fact]
    public void Ico_contains_every_size_and_loads_in_gdi()
    {
        var bytes = WpfHost.Run(() =>
        {
            using var stream = new MemoryStream();
            IcoWriter.Write(stream, IconRenderer.IcoSizes.Select(IconRenderer.Render).ToList());
            return stream.ToArray();
        });
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        Assert.Equal(IconRenderer.IcoSizes.Length, BitConverter.ToUInt16(bytes, 4));

        // GDI (tray icon) reads the bitmap entries…
        foreach (var size in new[] { 16, 24, 32, 48 })
        {
            using var icon = new System.Drawing.Icon(new MemoryStream(bytes), size, size);
            Assert.Equal(size, icon.Width);
        }

        // …and the shell/WPF decoder sees every size, including the PNG-compressed 256 px one.
        var sizes = WpfHost.Run(() =>
        {
            var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(new MemoryStream(bytes),
                System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            return decoder.Frames.Select(f => f.PixelWidth).OrderBy(w => w).ToList();
        });
        Assert.Equal(IconRenderer.IcoSizes, sizes);
    }

    [Fact]
    public void The_shipped_icon_has_every_size()
    {
        // Tests run from %LOCALAPPDATA%\ClementineBuild, so the repo root is baked in at build time.
        var root = typeof(IconTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!;
        var ico = Path.Combine(root, "src", "Clementine.App", "assets", "clementine.ico");
        Assert.True(File.Exists(ico), ico);
        var bytes = File.ReadAllBytes(ico);
        Assert.Equal(IconRenderer.IcoSizes.Length, BitConverter.ToUInt16(bytes, 4));
        using var icon = new System.Drawing.Icon(ico, 16, 16);
        Assert.Equal(16, icon.Width);
    }
}

public class GlyphTests
{
    [Fact]
    public void Every_glyph_exists_in_the_icon_font()
    {
        WpfHost.Run(() =>
        {
            var family = new FontFamily("Segoe Fluent Icons");
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            Assert.True(typeface.TryGetGlyphTypeface(out var glyphs), "Segoe Fluent Icons is installed");
            var missing = Glyphs.All.Where(g => !glyphs.CharacterToGlyphMap.ContainsKey(g[0])).Select(g => $"U+{(int)g[0]:X4}").ToList();
            Assert.True(missing.Count == 0, "Missing glyphs: " + string.Join(", ", missing));
        });
    }

    [Fact]
    public void Every_shared_icon_key_has_a_glyph()
    {
        var unmapped = ProviderContract.IconKeys.Where(k => !Glyphs.IsKnownIconKey(k)).ToList();
        Assert.True(unmapped.Count == 0, "No glyph for: " + string.Join(", ", unmapped));
    }
}

public class OptionsDialogTests
{
    static readonly OptionSchema Schema = new("Every field",
    [
        new NumberField("fps", "Frame rate", 30, 1, 120) { Unit = "fps" },
        new ChoiceField("audio", "Audio", [new OptionChoice("none", "None"), new OptionChoice("pcm", "PCM")], "none"),
        new ChoiceField("codec", "Codec", [new OptionChoice("h264", "H.264 MP4"), new OptionChoice("prores", "ProRes 4444 α"), new OptionChoice("hap", "HAP Alpha"), new OptionChoice("dxv", "DXV")], "prores"),
        new BoolField("loop", "Loop", false),
        new NumberField("repeat", "Repeat", 2, 1, 16) { VisibleWhen = new OptionCondition("loop", "true") },
        new TimeField("hold", "Hold", TimeSpan.FromSeconds(1.5)),
        new TextField("notes", "Notes", "") { Multiline = true },
        new TextField("password", "Password", "") { IsPassword = true },
        new FolderField("folder", "Folder") { VisibleWhen = new OptionCondition("loop", "true") },
    ]);

    [Fact]
    public void Builds_an_editor_for_every_field_and_honours_visible_when()
    {
        using var ws = new TestWorkspace("dialog");
        var input = InputFile.FromPath(ws.WriteText("a.png", "x"));
        WpfHost.Run(() =>
        {
            var dialog = new OptionsDialog(Schema, [input], OptionMerging.Prefill(Schema, null, null), "frames");
            var panel = (StackPanel)dialog.FindName("FieldsPanel");
            Assert.Equal(Schema.Fields.Count, panel.Children.Count);
            Assert.Equal(Visibility.Collapsed, panel.Children[4].Visibility); // repeat hidden until loop is on
            Assert.True(((Button)dialog.FindName("ConfirmButton")).IsEnabled);

            dialog.Model.Set("loop", true);
            Assert.Equal(Visibility.Visible, panel.Children[4].Visibility);
            Assert.False(((Button)dialog.FindName("ConfirmButton")).IsEnabled); // folder now required
            dialog.Model.Set("folder", ws.Root);
            Assert.True(((Button)dialog.FindName("ConfirmButton")).IsEnabled);
            Assert.Equal("Convert", ((Button)dialog.FindName("ConfirmButton")).Content);
            dialog.Close();
        });
    }

    [Fact]
    public void Typing_bad_values_blocks_confirm_until_fixed()
    {
        using var ws = new TestWorkspace("dialog");
        var input = InputFile.FromPath(ws.WriteText("a.png", "x"));
        WpfHost.Run(() =>
        {
            var dialog = new OptionsDialog(Schema, [input], OptionMerging.Prefill(Schema, null, null), "frames");
            Assert.True(dialog.TypeInto("hold", "1:7x"));
            Assert.False(dialog.CanConfirm);
            Assert.True(dialog.TypeInto("hold", "01:07.250"));
            Assert.True(dialog.CanConfirm);
            Assert.Equal(TimeSpan.FromSeconds(67.25), dialog.Model.Values["hold"]);

            Assert.True(dialog.TypeInto("fps", "500"));
            Assert.False(dialog.CanConfirm);
            Assert.True(dialog.TypeInto("fps", "24"));
            Assert.True(dialog.CanConfirm);
            Assert.Equal(24.0, dialog.Model.Values["fps"]);
            dialog.Close();
        });
    }

    [Theory]
    [InlineData(new[] { "None", "PCM" }, true)]
    [InlineData(new[] { "Fit", "Fill", "Stretch" }, true)]
    [InlineData(new[] { "H.264 MP4", "ProRes 4444 α", "HAP Alpha", "DXV" }, false)]
    [InlineData(new[] { "1", "2", "3", "4", "5" }, false)]
    public void Short_choices_become_segments(string[] labels, bool segmented)
    {
        var field = new ChoiceField("x", "X", labels.Select(l => new OptionChoice(l, l)).ToList(), labels[0]);
        Assert.Equal(segmented, ChoiceEditor.UseSegments(field));
    }
}

public class ToastCardTests
{
    [Fact]
    public void Cards_render_every_kind_without_errors()
    {
        WpfHost.Run(() =>
        {
            var states = new[]
            {
                new ToastState { Kind = ToastKind.Running, Title = "Converting", Progress = 0.4, Status = "40% · Encoding…" },
                new ToastState { Kind = ToastKind.Running, Title = "Reading", Progress = null },
                new ToastState { Kind = ToastKind.Succeeded, Title = "Converted", Outputs = [@"C:\a.mov"], Warnings = ["No audio."] },
                new ToastState { Kind = ToastKind.Succeeded, Title = "Info", Text = "line 1\nline 2", TextTitle = "Media info" },
                new ToastState { Kind = ToastKind.Failed, Title = "Couldn't convert", ErrorMessage = "Broken.", ErrorDetails = "stack" },
                ToastState.Info("Hello", "World", "How it works"),
            };
            foreach (var state in states)
            {
                var card = new ToastCard();
                card.Apply(state);
                card.Measure(new Size(400, double.PositiveInfinity));
                Assert.True(card.DesiredSize.Height > 40);
            }
        });
    }
}

public class ShelfStoreTests
{
    [Fact]
    public void Pins_existing_items_once_and_persists_them()
    {
        using var ws = new TestWorkspace("shelf");
        var file = ws.WriteText("clip.mov", "x");
        var folder = Directory.CreateDirectory(ws.Path("Renders")).FullName;
        var json = ws.Path("shelf.json");

        WpfHost.Run(() =>
        {
            var store = new ShelfStore(json, ws.Log);
            Assert.Equal(2, store.Add([file, folder, file, ws.Path("missing.txt")]));
            Assert.Equal(2, store.Items.Count);

            var reloaded = new ShelfStore(json, ws.Log);
            Assert.Equal([file, folder], reloaded.Items.Select(i => i.Path));

            File.Delete(file);
            reloaded.PruneMissing();
            Assert.Single(reloaded.Items);
            reloaded.Clear();
            Assert.Empty(new ShelfStore(json, ws.Log).Items);
        });
    }
}
