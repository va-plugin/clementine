using System.Windows.Threading;
using Clementine.App.Interop;
using Clementine.Core;

namespace Clementine.App.Actions;

/// <summary>The real <see cref="IShellActions"/>: marshals to the UI thread and talks to Explorer, the clipboard and the shelf.</summary>
public sealed class DesktopShellActions : IShellActions
{
    readonly Dispatcher _dispatcher;
    readonly ILog _log;
    readonly Action<IReadOnlyList<string>> _pin;

    public DesktopShellActions(Dispatcher dispatcher, ILog log, Action<IReadOnlyList<string>> pinToShelf)
    {
        _dispatcher = dispatcher;
        _log = log;
        _pin = pinToShelf;
    }

    public void SetClipboardText(string text)
    {
        var ok = _dispatcher.Invoke(() => ClipboardText.TrySet(text));
        if (!ok)
            throw new OperationException("Another app is holding the clipboard. Try again in a moment.");
    }

    public void Reveal(IReadOnlyList<string> paths) => ExplorerShell.Reveal(paths, _log);

    public void OpenTerminal(string directory) => ExplorerShell.OpenTerminal(directory);

    public void OpenWith(string file) =>
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                ExplorerShell.OpenWith(file, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                _log.Warn("Open with failed", ex);
            }
        });

    public void PinToShelf(IReadOnlyList<string> paths) => _dispatcher.Invoke(() => _pin(paths));
}
