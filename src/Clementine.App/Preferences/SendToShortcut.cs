using System.IO;
using Clementine.App.Interop;

namespace Clementine.App.Preferences;

/// <summary>
/// The "Send to → Clementine" shortcut (<c>%APPDATA%\Microsoft\Windows\SendTo\Clementine.lnk</c>, runs
/// <c>Clementine.exe --wheel</c> with the selected files). The folder is injectable for tests.
/// </summary>
public sealed class SendToShortcut
{
    public SendToShortcut(string sendToDirectory, string exePath)
    {
        Directory = sendToDirectory;
        ExePath = exePath;
    }

    public static string DefaultDirectory => Environment.GetFolderPath(Environment.SpecialFolder.SendTo);

    public string Directory { get; }

    public string ExePath { get; }

    public string ShortcutPath => Path.Combine(Directory, "Clementine.lnk");

    public bool Exists => File.Exists(ShortcutPath);

    public void Create()
    {
        System.IO.Directory.CreateDirectory(Directory);
        ShellLink.Create(ShortcutPath, ExePath, "--wheel", "Convert with Clementine");
    }

    public void Remove()
    {
        if (File.Exists(ShortcutPath))
            File.Delete(ShortcutPath);
    }
}
