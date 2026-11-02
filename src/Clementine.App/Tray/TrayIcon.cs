using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Clementine.App.Branding;
using Clementine.App.Theme;
using WinForms = System.Windows.Forms;

namespace Clementine.App.Tray;

/// <summary>What the tray menu can ask the shell to do.</summary>
public sealed record TrayCommands(Action OpenMain, Action OpenJobs, Action TogglePause, Action OpenSettings, Action OpenHelp, Action Quit,
    Action? RestartToUpdate = null);

/// <summary>
/// The notification-area icon (WinForms <see cref="WinForms.NotifyIcon"/> — the app's only WinForms use) with a
/// dark WPF menu: Open Clementine, Jobs, Pause/Resume wheel, Settings, Help, Quit, and "Restart to update (0.1.3)" at
/// the top while a downloaded update waits. Left-click opens the main window.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly TrayCommands _commands;
    ContextMenu? _menu;
    MenuItem? _pauseItem;
    MenuItem? _updateItem;
    Separator? _updateSeparator;
    Window? _menuOwner;
    bool _paused;
    string? _updateVersion;

    public TrayIcon(TrayCommands commands)
    {
        _commands = commands;
        // The WPF menu is built on the first right-click: creating it is the first use of the shell's control styles
        // and took most of the time before the icon appeared (0.6-1.4 s on a cold start).
        var size = Math.Max(16, WinForms.SystemInformation.SmallIconSize.Width);
        _icon = new WinForms.NotifyIcon
        {
            Icon = AppIcon.CreateTrayIcon(size),
            Text = "Clementine",
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                commands.OpenMain();
            else if (e.Button == WinForms.MouseButtons.Right)
                ShowMenu();
        };
    }

    public bool IsPaused => _paused;

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (_pauseItem is not null)
            ShowPauseState(_pauseItem, paused);
        if (_updateVersion is null)
            _icon.Text = paused ? "Clementine — wheel paused" : "Clementine";
    }

    /// <summary>Shows "Restart to update (<paramref name="version"/>)" in the menu, or hides it for null.</summary>
    public void SetUpdateReady(string? version)
    {
        _updateVersion = version;
        if (_updateItem is not null && _updateSeparator is not null)
            ShowUpdateState(_updateItem, _updateSeparator, version);
        _icon.Text = version is not null ? $"Clementine — {version} ready to install"
            : _paused ? "Clementine — wheel paused" : "Clementine";
    }

    /// <summary>Whether the menu has been built yet (it is built on the first right-click).</summary>
    internal bool HasMenu => _menu is not null;

    static void ShowUpdateState(MenuItem item, Separator separator, string? version)
    {
        item.Header = $"Restart to update ({version})";
        item.Visibility = separator.Visibility = version is null ? Visibility.Collapsed : Visibility.Visible;
    }

    static void ShowPauseState(MenuItem item, bool paused)
    {
        item.Header = paused ? "Resume wheel" : "Pause wheel";
        ((TextBlock)item.Icon).Text = paused ? Glyphs.Play : Glyphs.Pause;
    }

    ContextMenu Menu()
    {
        if (_menu is null)
        {
            (_menu, _pauseItem) = BuildMenu(_commands);
            ShowPauseState(_pauseItem, _paused);
            _updateItem = _menu.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, UpdateTag));
            _updateSeparator = _menu.Items.OfType<Separator>().FirstOrDefault(i => Equals(i.Tag, UpdateTag));
            if (_updateItem is not null && _updateSeparator is not null)
                ShowUpdateState(_updateItem, _updateSeparator, _updateVersion);
            _menu.Closed += (_, _) => _menuOwner?.Hide();
        }
        return _menu;
    }

    const string UpdateTag = "update";

    /// <summary>
    /// The menu, built standalone so snapshots can render it. The update item (and its separator) exist only when
    /// <see cref="TrayCommands.RestartToUpdate"/> is set; they start hidden unless <paramref name="updateVersion"/> is given.
    /// </summary>
    public static (ContextMenu Menu, MenuItem PauseItem) BuildMenu(TrayCommands commands, string? updateVersion = null)
    {
        var menu = new ContextMenu();
        if (commands.RestartToUpdate is { } restart)
        {
            var update = Item("Restart to update", Glyphs.Download, restart);
            update.Tag = UpdateTag;
            ((TextBlock)update.Icon).Foreground = ShellTheme.Brush("Clementine.AccentLight");
            var separator = new Separator { Tag = UpdateTag };
            ShowUpdateState(update, separator, updateVersion);
            menu.Items.Add(update);
            menu.Items.Add(separator);
        }
        menu.Items.Add(Item("Open Clementine", Glyphs.OpenExternal, commands.OpenMain, bold: true));
        menu.Items.Add(Item("Jobs", Glyphs.History, commands.OpenJobs));
        menu.Items.Add(new Separator());
        var pause = Item("Pause wheel", Glyphs.Pause, commands.TogglePause);
        menu.Items.Add(pause);
        menu.Items.Add(Item("Settings", Glyphs.Settings, commands.OpenSettings));
        menu.Items.Add(Item("Help", Glyphs.Help, commands.OpenHelp));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit", Glyphs.Power, commands.Quit));
        return (menu, pause);
    }

    static MenuItem Item(string header, string glyph, Action action, bool bold = false)
    {
        var item = new MenuItem
        {
            Header = header,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Icon = new TextBlock { Text = glyph, Style = (Style)Application.Current.FindResource("Shell.Glyph"), FontSize = 14, Foreground = ShellTheme.Brush("Clementine.TextSecondary") },
        };
        item.Click += (_, _) => action();
        return item;
    }

    void ShowMenu()
    {
        // A tiny owner window that we activate first, so clicking anywhere else closes the menu.
        _menuOwner ??= new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            Topmost = true,
        };
        var menu = Menu();
        _menuOwner.Show();
        _menuOwner.Activate();
        menu.PlacementTarget = _menuOwner;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>Builds the menu as the first right-click does, without opening it (tests).</summary>
    internal ContextMenu MenuForTest => Menu();

    /// <summary>The Pause/Resume item's text once the menu exists (tests).</summary>
    internal string? PauseItemHeader => _pauseItem?.Header as string;

    /// <summary>The update item's text when it is showing, once the menu exists (tests).</summary>
    internal string? VisibleUpdateItemHeader => _updateItem is { Visibility: Visibility.Visible } item ? item.Header as string : null;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
        _menuOwner?.Close();
    }
}
