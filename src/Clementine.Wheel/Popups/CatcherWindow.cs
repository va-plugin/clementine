using System.Windows;
using System.Windows.Media;
using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.Wheel;

/// <summary>
/// An almost invisible (alpha 1/255) drop target that follows the cursor while a Shift-drag is armed. Only when an
/// OLE drag carrying files enters it do we know files are being dragged — then the wheel opens. Anything else (text,
/// browser images, window moves) is ignored, and the catcher steps aside for the rest of that drag.
/// </summary>
internal sealed class CatcherWindow : PopupWindow
{
    /// <summary>Side of the catcher in DIPs.</summary>
    public const double SizeDip = 96;

    readonly ILog _log;

    public CatcherWindow(ILog log)
        : base(activatable: false)
    {
        _log = log;
        Title = "Clementine drop catcher";
        Width = Height = SizeDip;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    /// <summary>Files were dragged into the catcher (raised after the OLE call returns, on the UI thread).</summary>
    public event Action<IReadOnlyList<string>, ScreenPoint>? FilesEntered;

    /// <summary>A drag without files (or one that can't be copied) entered: the catcher should stay out of the way for the rest of this drag.</summary>
    public event Action? ForeignDragEntered;

    /// <summary>A drop landed on the catcher itself (rare: the wheel opened but the pointer never moved).</summary>
    public event Action<DragEventArgs>? Dropped;

    public void ShowAt(ScreenPoint point)
    {
        if (!IsVisible)
            Show();
        MoveTo(point);
    }

    public void MoveTo(ScreenPoint point)
    {
        if (IsVisible)
            PlaceCentred(point, SizeDip, keepInsideRadiusDip: 0);
    }

    public void HideCatcher()
    {
        if (IsVisible)
            Hide();
    }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        try
        {
            if (!IsVisible)
                return; // A late call after the catcher stepped aside: never open a wheel for it.
            var files = DragData.Files(e.Data);
            // A source that won't allow a copy (Move-only) could never drop on the wheel: step aside as for text.
            if (files is not null && (e.AllowedEffects & DragDropEffects.Copy) != 0)
            {
                e.Effects = DropEffects.CopyOrNone(accept: true, e.AllowedEffects);
                var at = NativeMethods.CursorPosition();
                // Return to the drag source immediately; open the wheel right after.
                Dispatcher.BeginInvoke(() => FilesEntered?.Invoke(files, at));
            }
            else
            {
                Dispatcher.BeginInvoke(() => ForeignDragEntered?.Invoke());
            }
        }
        catch (Exception ex)
        {
            e.Effects = DragDropEffects.None;
            _log.Warn("Catcher DragEnter failed", ex);
        }
        e.Effects = DropEffects.Sanitize(e.Effects, e.AllowedEffects);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            e.Effects = DropEffects.CopyOrNone(DragData.HasFiles(e.Data), e.AllowedEffects);
        }
        catch (Exception ex)
        {
            e.Effects = DragDropEffects.None;
            _log.Warn("Catcher DragOver failed", ex);
        }
        e.Effects = DropEffects.Sanitize(e.Effects, e.AllowedEffects);
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        try
        {
            Dropped?.Invoke(e);
        }
        catch (Exception ex)
        {
            e.Effects = DragDropEffects.None;
            _log.Warn("Catcher Drop failed", ex);
        }
        // Whatever the forwarded drop did: only Copy or None goes back (a Move/Link bit could make Explorer act on
        // the originals).
        e.Effects = DropEffects.Sanitize(e.Effects, e.AllowedEffects);
    }
}
