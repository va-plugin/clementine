using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clementine.App.Interop;
using Clementine.App.Theme;
using Clementine.Core;

namespace Clementine.App.Shelf;

/// <summary>
/// Floating shelf: holds files you pinned so you can drag them out again later (FileDrop, copy/link only).
/// Drop files onto it to pin them. Double-click opens an item.
/// </summary>
public partial class ShelfWindow : Window
{
    readonly ShelfStore _store;
    readonly ILog _log;
    Point? _dragStart;
    bool _draggingOut;

    public ShelfWindow(ShelfStore store, ILog log)
    {
        InitializeComponent();
        ShellTheme.ApplyWindow(this, framed: false);
        _store = store;
        _log = log;
        List.ItemsSource = store.Items;
        store.Items.CollectionChanged += OnItemsChanged;
        Closed += (_, _) => store.Items.CollectionChanged -= OnItemsChanged;
        UpdateState();

        Header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
        CloseButton.Click += (_, _) => Hide();
        ClearButton.Click += (_, _) => _store.Clear();
        List.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(List);
        List.PreviewMouseMove += OnListMouseMove;
        List.MouseDoubleClick += (_, _) =>
        {
            if (List.SelectedItem is ShelfItem item)
                OpenItem(item);
        };
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete)
            {
                foreach (var item in List.SelectedItems.OfType<ShelfItem>().ToList())
                    _store.Remove(item);
            }
        };
        DragOver += OnDragOver;
        DragEnter += OnDragOver;
        Drop += OnDrop;
    }

    /// <summary>Shows the shelf near the bottom-right of the cursor's monitor (first time) and brings it forward.</summary>
    public void ShowShelf()
    {
        if (!IsVisible)
        {
            Show();
            var monitor = ScreenGeometry.MonitorAt(ScreenGeometry.CursorPosition());
            UpdateLayout();
            var (width, height) = ScreenGeometry.PixelSize(this);
            ScreenGeometry.MoveTo(this, monitor.WorkArea.X + monitor.WorkArea.Width - width - 24,
                monitor.WorkArea.Y + Math.Max(0, (monitor.WorkArea.Height - height) / 2), topmost: true);
        }
        Activate();
    }

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateState();

    void UpdateState()
    {
        var count = _store.Items.Count;
        CountText.Text = count switch
        {
            0 => "Drop files here to keep them handy",
            1 => "1 item · drag it out to use it",
            _ => $"{count} items · drag out to use",
        };
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // Nothing to clear: hide the button (a disabled one squeezed the subtitle onto two lines).
        ClearButton.IsEnabled = count > 0;
        ClearButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _draggingOut)
            return;
        var now = e.GetPosition(List);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var items = List.SelectedItems.OfType<ShelfItem>().Where(i => i.Exists).ToList();
        if (items.Count == 0)
            return;
        var data = new DataObject(DataFormats.FileDrop, items.Select(i => i.Path).ToArray());
        _draggingOut = true;
        try
        {
            // Copy or link only: dragging off the shelf never moves the original away.
            DragDrop.DoDragDrop(List, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _log.Warn("Shelf drag failed", ex);
        }
        finally
        {
            _draggingOut = false;
            _dragStart = null;
        }
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_draggingOut && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        if (_draggingOut || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;
        _store.Add(paths);
    }

    void OnRowButton(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ShelfItem item, Tag: string action })
            return;
        if (action == "remove")
            _store.Remove(item);
        else
            ExplorerShell.Reveal([item.Path], _log);
    }

    void OpenItem(ShelfItem item)
    {
        try
        {
            ExplorerShell.Open(item.Path, _log);
        }
        catch (OperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Clementine", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
