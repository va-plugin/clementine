using Microsoft.Win32;

namespace Clementine.App.Preferences;

/// <summary>
/// Per-user registry integration: "launch at login" (HKCU Run value) and the Explorer context-menu verb
/// "Convert with Clementine" for files and folders. Paths are injectable so tests run against a throw-away key
/// under <c>HKCU\Software\ClementineTests</c>. Only called when the user flips a toggle.
/// </summary>
public sealed class ShellRegistration
{
    public const string RunValueName = "Clementine";
    public const string VerbKeyName = "Clementine";
    public const string VerbText = "Convert with Clementine";

    readonly RegistryKey _hive;
    readonly string _runKeyPath;
    readonly string _classesPath;

    public ShellRegistration(string exePath, RegistryKey hive, string runKeyPath, string classesPath)
    {
        ExePath = exePath;
        _hive = hive;
        _runKeyPath = runKeyPath;
        _classesPath = classesPath;
    }

    /// <summary>The real locations: HKCU\…\CurrentVersion\Run and HKCU\Software\Classes.</summary>
    public static ShellRegistration ForCurrentUser(string exePath) =>
        new(exePath, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Classes");

    /// <summary>Clementine.exe of the running process.</summary>
    public static string CurrentExePath =>
        Environment.ProcessPath is { } path && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? path
            : System.IO.Path.Combine(AppContext.BaseDirectory, "Clementine.exe");

    public string ExePath { get; }

    public string LaunchAtLoginCommand => $"\"{ExePath}\" --background";

    public string WheelCommand => $"\"{ExePath}\" --wheel \"%1\"";

    string FileVerbPath => $@"{_classesPath}\*\shell\{VerbKeyName}";

    string FolderVerbPath => $@"{_classesPath}\Directory\shell\{VerbKeyName}";

    public bool IsLaunchAtLoginEnabled()
    {
        using var key = _hive.OpenSubKey(_runKeyPath);
        return key?.GetValue(RunValueName) is string { Length: > 0 };
    }

    public void SetLaunchAtLogin(bool enabled)
    {
        if (enabled)
        {
            using var key = _hive.CreateSubKey(_runKeyPath, writable: true);
            key.SetValue(RunValueName, LaunchAtLoginCommand, RegistryValueKind.String);
        }
        else
        {
            using var key = _hive.OpenSubKey(_runKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    public bool IsContextMenuInstalled()
    {
        using var files = _hive.OpenSubKey(FileVerbPath + @"\command");
        using var folders = _hive.OpenSubKey(FolderVerbPath + @"\command");
        return files?.GetValue(null) is string && folders?.GetValue(null) is string;
    }

    public void SetContextMenu(bool enabled)
    {
        if (enabled)
        {
            WriteVerb(FileVerbPath);
            WriteVerb(FolderVerbPath);
        }
        else
        {
            _hive.DeleteSubKeyTree(FileVerbPath, throwOnMissingSubKey: false);
            _hive.DeleteSubKeyTree(FolderVerbPath, throwOnMissingSubKey: false);
        }
    }

    void WriteVerb(string path)
    {
        using var verb = _hive.CreateSubKey(path, writable: true);
        verb.SetValue("MUIVerb", VerbText, RegistryValueKind.String);
        verb.SetValue("Icon", $"\"{ExePath}\",0", RegistryValueKind.String);
        // "Player" lifts Explorer's 15-item limit; each item still starts a process, which the app aggregates.
        verb.SetValue("MultiSelectModel", "Player", RegistryValueKind.String);
        using var command = verb.CreateSubKey("command", writable: true);
        command.SetValue(null, WheelCommand, RegistryValueKind.String);
    }
}
