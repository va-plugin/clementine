using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Clementine.App.Interop;

/// <summary>Creates and reads .lnk shortcuts through the shell's ShellLink COM object.</summary>
internal static class ShellLink
{
    public static void Create(string shortcutPath, string target, string arguments, string description, string? iconPath = null)
    {
        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetDescription(description);
            link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(target) ?? "");
            link.SetIconLocation(iconPath ?? target, 0);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>Target path and arguments of an existing shortcut.</summary>
    public static (string Target, string Arguments) Read(string shortcutPath)
    {
        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            ((IPersistFile)link).Load(shortcutPath, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var arguments = new StringBuilder(1024);
            link.GetArguments(arguments, arguments.Capacity);
            return (target.ToString(), arguments.ToString());
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLinkObject;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxChars, IntPtr findData, uint flags);

        void GetIDList(out IntPtr pidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxChars);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxChars);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxChars);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxChars, out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

        void Resolve(IntPtr hwnd, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
