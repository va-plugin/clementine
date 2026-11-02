using System.Globalization;
using Clementine.Core;
using Clementine.Engines.Media.Audio;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;
using Clementine.Engines.Media.Video;

namespace Clementine.Engines.Media.Catalog;

/// <summary>The Tools-wheel entries for audio files. Results keep the input's format, tags and cover art.</summary>
internal static class AudioTools
{
    /// <summary>areverse holds the whole track in memory; beyond this the machine would struggle.</summary>
    static readonly TimeSpan MaxReverse = TimeSpan.FromMinutes(20);

    public static IEnumerable<IOperation> Create(MediaToolkit kit)
    {
        yield return new AudioEditOperation(Info("audio.trim", "Trim", null, 10, "Trim audio",
            "Keeps the part between a start and an end time, without re-encoding.", "trim"), kit, "Trimming…",
            (source, options) =>
            {
                var (start, length, warning) = TrimVideoOperation.Range(options, source.DurationOrZero);
                return new AudioEditPlan(new AudioEdit { Start = start, Duration = length, AllowCopy = true }, "trimmed", warning is null ? null : [warning]);
            },
            new OptionSchema("Trim",
            [
                new TimeField("start", "Start", TimeSpan.Zero),
                new TimeField("end", "End", TimeSpan.Zero) { Help = "Defaults to the end (0 also means the end)." },
            ])
            { ConfirmLabel = "Trim" },
            endDefaultsToDuration: true);

        yield return new NormalizeOperation(Info("audio.normalize.streaming", "Normalize", "Streaming −14 LUFS", 20, "Normalize for streaming (−14 LUFS)",
            "Evens out loudness to the Spotify / YouTube / Apple Music level.", "normalize"), kit, NormalizeTarget.Streaming);
        yield return new NormalizeOperation(Info("audio.normalize.podcast", "Normalize", "Podcast −16 LUFS", 21, "Normalize for podcasts (−16 LUFS)",
            "The loudness podcast apps expect.", "normalize"), kit, NormalizeTarget.Podcast);
        yield return new NormalizeOperation(Info("audio.normalize.broadcast", "Normalize", "Broadcast −23 LUFS", 22, "Normalize for broadcast (−23 LUFS)",
            "EBU R128 loudness for TV and radio.", "normalize"), kit, NormalizeTarget.Broadcast);
        yield return new NormalizeOperation(Info("audio.normalize.peak", "Normalize", "Peak 0 dB", 23, "Normalize peak to 0 dB",
            "Raises (or lowers) the level so the loudest peak sits just under 0 dB.", "normalize"), kit, NormalizeTarget.Peak);

        yield return new AudioEditOperation(Info("audio.volume", "Volume", null, 30, "Change volume",
            "Makes the audio louder or quieter by a number of decibels.", "volume"), kit, "Changing volume…",
            (_, options) =>
            {
                var db = Math.Clamp(options.GetDouble("db", 3), -60, 60);
                if (Math.Abs(db) < 0.01)
                    throw new OperationException("Choose a change other than 0 dB.");
                var text = db.ToString("+0.#;-0.#", CultureInfo.InvariantCulture);
                return new AudioEditPlan(new AudioEdit { Filters = [$"volume={text}dB"] }, $"{text} dB",
                    db > 0 ? ["Raising the volume can clip loud passages; use Normalize for a safe maximum."] : null);
            },
            new OptionSchema("Volume", [new NumberField("db", "Change", 3, -30, 30) { Unit = "dB", Step = 0.5, Decimals = 1 }]) { ConfirmLabel = "Apply" });

        yield return Channels(kit, "audio.channels.mono", "Mono", 40, "Convert to mono", "Mixes all channels down to one.", "mono",
            _ => new AudioEdit { Channels = 1 });
        yield return Channels(kit, "audio.channels.stereo", "Stereo", 41, "Convert to stereo", "Makes two channels (mono is duplicated, surround is folded down).", "stereo",
            _ => new AudioEdit { Channels = 2 });
        yield return Channels(kit, "audio.channels.swap", "Swap L/R", 42, "Swap left and right", "Exchanges the left and right channels.", "swapped",
            audio => new AudioEdit { Filters = [SwapPan(audio)] }, needsStereo: true);
        yield return Channels(kit, "audio.channels.left", "Left only", 43, "Keep the left channel", "A mono file of just the left channel.", "left",
            _ => new AudioEdit { Filters = ["pan=mono|c0=c0"] }, needsStereo: true);
        yield return Channels(kit, "audio.channels.right", "Right only", 44, "Keep the right channel", "A mono file of just the right channel.", "right",
            _ => new AudioEdit { Filters = ["pan=mono|c0=c1"] }, needsStereo: true);

        yield return new AudioEditOperation(Info("audio.speed", "Speed", null, 50, "Change speed",
            "Plays faster or slower without changing the pitch.", "speed"), kit, "Changing speed…",
            (_, options) =>
            {
                var factor = Math.Clamp(options.GetDouble("factor", 1.25), 0.1, 10);
                return new AudioEditPlan(new AudioEdit { Filters = VideoGeometry.AtempoChain(factor), Speed = factor }, VideoGeometry.SpeedLabel(factor));
            },
            new OptionSchema("Speed", [new NumberField("factor", "Speed", 1.25, 0.1, 10) { Step = 0.05, Decimals = 2, Unit = "×" }]) { ConfirmLabel = "Apply" });

        yield return new AudioEditOperation(Info("audio.fade.default", "Fade", "In & out 2 s", 60, "Fade in and out (2 s)",
            "Two-second fade at the start and the end.", "fade"), kit, "Fading…",
            (source, _) => new AudioEditPlan(new AudioEdit { Filters = AudioFilters.Fades(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), source.DurationOrZero) }, "faded"));
        yield return new AudioEditOperation(Info("audio.fade.custom", "Fade", "Custom…", 61, "Fade in and out",
            "Fade lengths you choose for the start and the end.", "fade"), kit, "Fading…",
            (source, options) => new AudioEditPlan(new AudioEdit
            {
                Filters = AudioFilters.Fades(TimeSpan.FromSeconds(options.GetDouble("in", 2)), TimeSpan.FromSeconds(options.GetDouble("out", 2)), source.DurationOrZero),
            }, "faded"),
            new OptionSchema("Fade",
            [
                new NumberField("in", "Fade in", 2, 0, 600) { Unit = "s", Step = 0.5, Decimals = 1 },
                new NumberField("out", "Fade out", 2, 0, 600) { Unit = "s", Step = 0.5, Decimals = 1 },
            ])
            { ConfirmLabel = "Fade" });

        yield return new AudioEditOperation(Info("audio.bleep", "Bleep", null, 70, "Bleep",
            "Covers the time ranges you list with a beep (or silence).", "bleep"), kit, "Bleeping…",
            (source, options) =>
            {
                var ranges = AudioFilters.ParseRanges(options.GetStringOrNull("ranges"));
                var tone = options.GetString("sound", "tone") == "tone";
                return new AudioEditPlan(new AudioEdit { Graph = AudioFilters.BleepGraph(ranges, tone, source.Audio!, source.DurationOrZero) }, "bleeped");
            },
            new OptionSchema("Bleep",
            [
                new TextField("ranges", "Ranges", "") { Placeholder = "0:12.5-0:13.2, 1:05-1:06", Multiline = true },
                new ChoiceField("sound", "Cover with", [new OptionChoice("tone", "1 kHz beep"), new OptionChoice("silence", "Silence")], "tone"),
            ])
            { ConfirmLabel = "Bleep" });

        yield return new CompressAudioOperation(Info("audio.compress.smaller", "Compress", "Smaller", 80, "Compress audio",
            "A 128 kbps MP3 — small and plays everywhere.", "compress", ".mp3"), kit, targetSize: false);
        yield return new CompressAudioOperation(Info("audio.compress.target", "Compress", "Target size…", 81, "Compress audio to a target size",
            "Picks the MP3 bit rate that fits the size you choose.", "compress", ".mp3"), kit, targetSize: true);

        yield return new SplitAudioOperation(Info("audio.split", "Split", null, 90, "Split audio",
            "Cuts into equal parts, fixed-length pieces or at silences — saved in a folder, without re-encoding.", "split"), kit);
        yield return new JoinAudioOperation(Info("audio.join", "Join", null, 91, "Join audio",
            "Joins the files end to end in name order.", "join"), kit);

        yield return new AudioEditOperation(Info("audio.reverse", "Reverse", null, 100, "Reverse audio",
            "Plays the audio backwards.", "reverse"), kit, "Reversing…",
            (source, _) => source.DurationOrZero > MaxReverse
                ? throw new OperationException($"Reverse works on audio up to {MaxReverse.TotalMinutes:0} minutes long. Trim or split it first.")
                : new AudioEditPlan(new AudioEdit { Filters = ["areverse"] }, "reversed"));

        yield return new WaveformVideoOperation(Info("audio.waveform", "Waveform", null, 110, "Waveform video",
            "An MP4 of the sound with a moving waveform — for posting audio where only video is allowed.", "waveform", ".mp4", OperationWeight.Heavy), kit);

        yield return new RemoveMetadataOperation(Info("audio.metadata.remove", "Metadata", null, 120, "Remove metadata",
            "Strips tags, chapters and cover art without re-encoding.", "metadata"), kit, audio: true);
        yield return new MediaInfoOperation(Info("audio.info", "Info", null, 130, "Audio info",
            "Shows codec, sample rate, channels, bit rate and tags.", "info"), kit, audio: true);
    }

    static AudioEditOperation Channels(MediaToolkit kit, string id, string variant, int order, string title, string description, string suffix,
        Func<AudioStreamInfo, AudioEdit> edit, bool needsStereo = false) =>
        new(Info(id, "Channels", variant, order, title, description, "channels"), kit, "Changing channels…",
            (source, _) =>
            {
                var audio = source.Audio!;
                if (needsStereo && audio.Channels < 2)
                    throw new OperationException("This file is mono — it only has one channel.");
                return new AudioEditPlan(edit(audio), suffix);
            });

    /// <summary>Swaps the first two channels and passes any others (surround) through.</summary>
    static string SwapPan(AudioStreamInfo audio)
    {
        if (audio.Channels <= 2 || string.IsNullOrEmpty(audio.ChannelLayout))
            return "pan=stereo|c0=c1|c1=c0";
        var rest = Enumerable.Range(2, audio.Channels - 2).Select(i => $"|c{i}=c{i}");
        return $"pan={audio.ChannelLayout}|c0=c1|c1=c0{string.Concat(rest)}";
    }

    static OperationInfo Info(
        string id, string group, string? variant, int order, string title, string description, string icon,
        string? extension = null, OperationWeight weight = OperationWeight.Light) => new()
    {
        Id = id,
        Category = OperationCategory.Tool,
        Group = group,
        Variant = variant,
        Title = title,
        Description = description,
        IconKey = icon,
        OutputExtension = extension,
        Order = order,
        Weight = weight,
    };
}
