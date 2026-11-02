using Clementine.App.Options;
using Clementine.Core;

namespace Clementine.App.Tests;

public class OptionMergingTests
{
    static readonly OptionSchema Trim = new("Trim",
    [
        new TimeField("start", "Start", TimeSpan.Zero),
        new TimeField("end", "End", TimeSpan.FromSeconds(10)),
        new BoolField("precise", "Precise", false),
        new NumberField("quality", "Quality", 70, 10, 100) { Step = 5 },
        new ChoiceField("codec", "Codec", [new OptionChoice("h264", "H.264"), new OptionChoice("h265", "H.265")], "h264"),
        new TextField("password", "Password", "") { IsPassword = true },
        new TextField("name", "Name", "{name}"),
        new FolderField("folder", "Folder"),
    ]);

    [Fact]
    public void Defaults_then_remembered_then_dynamic_defaults()
    {
        var remembered = new Dictionary<string, string>
        {
            ["start"] = "2.5",
            ["end"] = "4",
            ["precise"] = "true",
            ["quality"] = "85",
            ["codec"] = "h265",
            ["folder"] = @"D:\Exports",
        };
        var dynamicDefaults = new OptionValues { ["end"] = TimeSpan.FromSeconds(42.5) };

        var values = OptionMerging.Prefill(Trim, remembered, dynamicDefaults);

        Assert.Equal(TimeSpan.FromSeconds(2.5), values["start"]);
        Assert.Equal(TimeSpan.FromSeconds(42.5), values["end"]); // this clip's duration beats last time's value
        Assert.Equal(true, values["precise"]);
        Assert.Equal(85.0, values["quality"]);
        Assert.Equal("h265", values["codec"]);
        Assert.Equal("{name}", values["name"]);
        Assert.Equal(@"D:\Exports", values["folder"]);
    }

    [Fact]
    public void Remembered_values_that_no_longer_fit_are_ignored()
    {
        var remembered = new Dictionary<string, string>
        {
            ["quality"] = "not a number",
            ["codec"] = "vp9",
            ["precise"] = "maybe",
            ["end"] = "-3",
        };
        var values = OptionMerging.Prefill(Trim, remembered, null);
        Assert.Equal(70.0, values["quality"]);
        Assert.Equal("h264", values["codec"]);
        Assert.Equal(false, values["precise"]);
        Assert.Equal(TimeSpan.FromSeconds(10), values["end"]);
    }

    [Fact]
    public void Numbers_are_clamped_and_rounded_to_the_field()
    {
        var values = OptionMerging.Prefill(Trim, new Dictionary<string, string> { ["quality"] = "250" }, null);
        Assert.Equal(100.0, values["quality"]);
        var field = new NumberField("x", "X", 1, 0, 10) { Decimals = 1 };
        Assert.Equal(2.3, OptionMerging.Coerce(field, "2.34"));
    }

    [Fact]
    public void Passwords_are_never_remembered_or_prefilled()
    {
        var values = OptionMerging.Prefill(Trim, new Dictionary<string, string> { ["password"] = "hunter2" }, null);
        Assert.Equal("", values["password"]);

        values["password"] = "secret";
        values["quality"] = 55.0;
        values["end"] = TimeSpan.FromSeconds(1.25);
        var remember = OptionMerging.ToRemember(Trim, values);
        Assert.False(remember.ContainsKey("password"));
        Assert.Equal("55", remember["quality"]);
        Assert.Equal("1.25", remember["end"]);
        Assert.Equal("false", remember["precise"]);
    }

    [Fact]
    public void Remembered_strings_round_trip_through_settings()
    {
        var values = OptionMerging.Prefill(Trim, null, null);
        values["quality"] = 90.0;
        values["start"] = TimeSpan.FromSeconds(3.125);
        var settings = new ClementineSettings();
        settings.RememberedOptions["video.trim"] = OptionMerging.ToRemember(Trim, values);

        var copy = settings.Clone();
        var reloaded = OptionMerging.Prefill(Trim, copy.RememberedOptions["video.trim"], null);
        Assert.Equal(90.0, reloaded["quality"]);
        Assert.Equal(TimeSpan.FromSeconds(3.125), reloaded["start"]);
    }
}

public class OptionFormModelTests
{
    static readonly OptionSchema Compress = new("Compress",
    [
        new ChoiceField("mode", "Target", [new OptionChoice("percent", "Quality"), new OptionChoice("size", "File size")], "percent"),
        new NumberField("quality", "Quality", 70, 10, 100) { VisibleWhen = new OptionCondition("mode", "percent") },
        new NumberField("size", "Size", 50, 1, 4000) { VisibleWhen = new OptionCondition("mode", "size"), Decimals = 1 },
        new BoolField("advanced", "Advanced", false),
        new TimeField("start", "Start", TimeSpan.Zero) { VisibleWhen = new OptionCondition("advanced", "true") },
        new FolderField("folder", "Folder") { VisibleWhen = new OptionCondition("advanced", "true") },
    ]);

    static OptionFormModel Create() => new(Compress, OptionMerging.Prefill(Compress, null, null));

    [Fact]
    public void Visible_when_follows_choices_and_toggles()
    {
        var model = Create();
        Assert.Equal(["mode", "quality", "advanced"], model.VisibleFields.Select(f => f.Key));

        model.Set("mode", "size");
        Assert.Equal(["mode", "size", "advanced"], model.VisibleFields.Select(f => f.Key));

        model.Set("advanced", true);
        Assert.Contains(model.VisibleFields, f => f.Key == "start");
        Assert.Contains(model.VisibleFields, f => f.Key == "folder");
    }

    [Fact]
    public void Hidden_required_fields_do_not_block_confirm()
    {
        var model = Create();
        Assert.True(model.IsValid); // the folder is hidden, so it isn't required yet
        model.Set("advanced", true);
        Assert.False(model.IsValid);
        Assert.Equal("Choose a folder.", model.Validate(Compress.Fields[5]));
        model.Set("folder", @"D:\Out");
        Assert.True(model.IsValid);
        model.Set("folder", "relative\\path");
        Assert.NotNull(model.Validate(Compress.Fields[5]));
    }

    [Fact]
    public void Number_text_is_validated_and_accepts_units_and_both_decimal_marks()
    {
        var model = Create();
        var quality = (NumberField)Compress.Fields[1];
        Assert.Null(model.SetFromText(quality, "85 "));
        Assert.Equal(85.0, model.Values["quality"]);
        Assert.NotNull(model.SetFromText(quality, "abc"));
        Assert.False(model.IsValid);
        Assert.NotNull(model.SetFromText(quality, "500"));
        Assert.Null(model.SetFromText(quality, "90"));
        Assert.True(model.IsValid);

        Assert.True(OptionFormModel.TryParseNumber("12.5 MB", "MB", out var mb));
        Assert.Equal(12.5, mb);
        Assert.True(OptionFormModel.TryParseNumber("12,5", null, out var comma));
        Assert.Equal(12.5, comma);
    }

    [Fact]
    public void Time_text_is_validated()
    {
        var model = Create();
        model.Set("advanced", true);
        var start = Compress.Fields[4];
        Assert.Null(model.SetFromText(start, "01:02.500"));
        Assert.Equal(TimeSpan.FromSeconds(62.5), model.Values["start"]);
        Assert.Equal("Use mm:ss.fff, for example 01:30.500.", model.SetFromText(start, "1:7x"));
        Assert.Equal("Use mm:ss.fff, for example 01:30.500.", model.Validate(start));
        Assert.Null(model.SetFromText(start, "75"));
        Assert.Equal(TimeSpan.FromSeconds(75), model.Values["start"]);
    }

    [Fact]
    public void Reset_restores_the_opening_values_and_raises_changes()
    {
        var model = Create();
        var changes = new List<string>();
        model.ValueChanged += (_, key) => changes.Add(key);
        model.Set("mode", "size");
        model.Set("size", 12.5);
        model.Reset();
        Assert.Equal("percent", model.Values["mode"]);
        Assert.Equal(50.0, model.Values["size"]);
        Assert.Contains("quality", changes);
    }
}
