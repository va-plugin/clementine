using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clementine.Core;

public enum OutputLocationMode
{
    /// <summary>Save results beside the original file (the default, like Tangerine-style converters).</summary>
    NextToOriginal,

    /// <summary>Save every result into <see cref="ClementineSettings.CustomOutputFolder"/>.</summary>
    CustomFolder,
}

/// <summary>Modifier keys that, held while dragging files, open a wheel.</summary>
[Flags]
public enum TriggerModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Windows = 8,
}

/// <summary>User preferences, persisted as JSON by <see cref="SettingsStore"/>. Treat instances as immutable snapshots.</summary>
public sealed class ClementineSettings
{
    public int Version { get; set; } = 1;

    // Output
    public OutputLocationMode OutputMode { get; set; } = OutputLocationMode.NextToOriginal;

    public string? CustomOutputFolder { get; set; }

    public bool RevealInExplorerWhenDone { get; set; }

    // Wheel & triggers
    public bool WheelEnabled { get; set; } = true;

    public TriggerModifiers ConvertModifiers { get; set; } = TriggerModifiers.Shift;

    public TriggerModifiers ToolsModifiers { get; set; } = TriggerModifiers.Shift | TriggerModifiers.Control;

    public TriggerModifiers ActionsModifiers { get; set; } = TriggerModifiers.Shift | TriggerModifiers.Alt;

    /// <summary>Global hotkey that opens the wheel for the files selected in Explorer ("" disables it).</summary>
    /// <remarks>Not Ctrl+Alt+…: on AltGr keyboard layouts that chord is AltGr and fires while typing.</remarks>
    public string SelectionHotkey { get; set; } = "Ctrl+Shift+Space";

    // Performance
    public int MaxConcurrentHeavyJobs { get; set; } = 2;

    /// <summary>Use NVENC / Quick Sync / AMF for H.264 and H.265 when the GPU supports it.</summary>
    public bool UseHardwareEncoding { get; set; } = true;

    // External tools (null = auto-detect)
    public string? FfmpegPath { get; set; }

    public string? LibreOfficePath { get; set; }

    public string? SevenZipPath { get; set; }

    // Video
    /// <summary>Sample rate for PCM audio in DXV / HAP / ProRes outputs (Resolume expects 48 kHz).</summary>
    public int VjAudioSampleRate { get; set; } = 48000;

    /// <summary>Convert variable-frame-rate sources to constant frame rate for intra-frame VJ codecs.</summary>
    public bool ForceConstantFrameRate { get; set; } = true;

    // App behaviour
    public bool LaunchAtLogin { get; set; }

    public bool PlaySounds { get; set; } = true;

    public bool ShowNotifications { get; set; } = true;

    public bool ExplorerContextMenu { get; set; }

    public bool SendToShortcut { get; set; }

    public bool FirstRunCompleted { get; set; }

    /// <summary>Look for a newer release on the project's GitHub page and offer it from the tray (installed copies only).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Last option values used per operation id (invariant strings), so dialogs reopen as the user left them.</summary>
    public Dictionary<string, Dictionary<string, string>> RememberedOptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ClementineSettings Clone() =>
        Deserialize(JsonSerializer.Serialize(this, JsonOptions)) ?? new ClementineSettings();

    internal static ClementineSettings? Deserialize(string json)
    {
        var settings = JsonSerializer.Deserialize<ClementineSettings>(json, JsonOptions);
        if (settings is not null)
        {
            // JSON round-trips drop dictionary comparers; operation ids and option keys are case-insensitive.
            settings.RememberedOptions = new Dictionary<string, Dictionary<string, string>>(
                (settings.RememberedOptions ?? []).ToDictionary(
                    kv => kv.Key,
                    kv => new Dictionary<string, string>(kv.Value ?? [], StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        }
        return settings;
    }
}
