namespace Clementine.App.Actions;

/// <summary>
/// Desktop side effects the actions need. Implementations must be callable from any thread
/// (operations run on worker threads); the app's implementation marshals to the UI thread.
/// </summary>
public interface IShellActions
{
    void SetClipboardText(string text);

    void Reveal(IReadOnlyList<string> paths);

    void OpenTerminal(string directory);

    void OpenWith(string file);

    void PinToShelf(IReadOnlyList<string> paths);
}
