using System.Runtime.InteropServices;
using System.Windows;

namespace Clementine.App.Interop;

/// <summary>Clipboard writes with retries (another app briefly holding the clipboard is common). UI thread only.</summary>
internal static class ClipboardText
{
    public static bool TrySet(string text)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        return false;
    }
}
