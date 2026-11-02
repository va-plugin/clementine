using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clementine.App.Branding;
using Clementine.App.Interop;
using Clementine.App.Options;
using Clementine.App.Preferences;
using Clementine.App.Theme;
using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.App.Main;

/// <summary>
/// The window behind the tray icon: a big drop zone, then Convert / Tools / Actions tiles for the chosen files
/// (same catalog queries as the wheel) and the recent jobs list.
/// </summary>
public partial class MainWindow : Window
{
    readonly HostServices _services;
    readonly Action _openSettings;
    readonly Action _openHelp;
    readonly ObservableCollection<JobRow> _jobs = [];
    readonly DispatcherTimer _refreshTimer;
    readonly EventHandler<Job> _jobEvent;
    readonly EventHandler _settingsChanged;
    readonly Func<bool> _ffmpegMissing;
    List<InputFile> _files = [];
    WheelMode _mode = WheelMode.Convert;
    double _tileWidth = 214;
    string? _ffmpegPath;

    /// <param name="ffmpegMissing">Whether FFmpeg is missing (default: <see cref="ToolStatus.IsFfmpegMissing"/>; snapshots fake it).</param>
    public MainWindow(HostServices services, Action openSettings, Action openHelp, Func<bool>? ffmpegMissing = null)
    {
        InitializeComponent();
        ShellTheme.ApplyWindow(this);
        _services = services;
        _openSettings = openSettings;
        _openHelp = openHelp;
        _ffmpegMissing = ffmpegMissing ?? (() => ToolStatus.IsFfmpegMissing(services.Tools));
        _ffmpegPath = services.Settings.Current.FfmpegPath;
        HeaderLogo.Source = AppIcon.Logo;
        DropLogo.Source = AppIcon.Logo;
        JobsList.ItemsSource = _jobs;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshJobs();
        };
        _jobEvent = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!_refreshTimer.IsEnabled)
                _refreshTimer.Start();
        });
        _settingsChanged = (_, _) => Dispatcher.BeginInvoke(OnSettingsChanged);
        services.Jobs.JobAdded += _jobEvent;
        services.Jobs.JobChanged += _jobEvent;
        services.Settings.Changed += _settingsChanged;
        Closed += (_, _) =>
        {
            services.Jobs.JobAdded -= _jobEvent;
            services.Jobs.JobChanged -= _jobEvent;
            services.Settings.Changed -= _settingsChanged;
        };

        SettingsButton.Click += (_, _) => _openSettings();
        HelpButton.Click += (_, _) => _openHelp();
        BrowseButton.Click += (_, _) => BrowseFiles(add: false);
        AddMoreButton.Click += (_, _) => BrowseFiles(add: true);
        BrowseFolderButton.Click += (_, _) => BrowseFolders();
        ClearSelectionButton.Click += (_, _) => ShowFiles([]);
        ClearJobsButton.Click += (_, _) =>
        {
            _services.Jobs.ClearFinished();
            RefreshJobs();
        };
        GetFfmpegButton.Click += (_, _) => ExplorerShell.OpenUrl(ToolStatus.FfmpegDownloadUrl, _services.Log);
        ChooseFfmpegButton.Click += (_, _) => ChooseFfmpeg();
        ConvertTab.Checked += (_, _) => SelectMode(WheelMode.Convert);
        ToolsTab.Checked += (_, _) => SelectMode(WheelMode.Tools);
        ActionsTab.Checked += (_, _) => SelectMode(WheelMode.Actions);

        TilesScroller.SizeChanged += (_, _) => LayoutTiles();
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DragOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;

        BuildShortcutHints();
        RefreshJobs();
    }

    /// <summary>The files currently shown (empty = drop zone).</summary>
    public IReadOnlyList<InputFile> Files => _files;

    /// <summary>Shows the tiles for <paramref name="files"/> (or the drop zone when empty).</summary>
    public void ShowFiles(IReadOnlyList<InputFile> files)
    {
        _files = files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var any = _files.Count > 0;
        DropZone.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        FilesView.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any)
            return;

        SelectionIcon.Source = ShellIcons.For(_files[0].Path, 36);
        SelectionTitle.Text = InputSummary.Describe(_files);
        SelectionDetails.Text = DescribeSelection(_files);
        UpdateTabHeaders();
        BuildTiles();
    }

    public void SelectMode(WheelMode mode)
    {
        _mode = mode;
        var tab = mode switch
        {
            WheelMode.Tools => ToolsTab,
            WheelMode.Actions => ActionsTab,
            _ => ConvertTab,
        };
        if (tab.IsChecked != true)
            tab.IsChecked = true;
        BuildTiles();
    }

    /// <summary>Brings the jobs list into view (tray menu "Jobs").</summary>
    public void FocusJobs()
    {
        RefreshJobs();
        if (_jobs.Count > 0)
        {
            JobsList.ScrollIntoView(_jobs[0]);
            JobsList.Focus();
        }
    }

    public void RefreshJobs()
    {
        var snapshot = _services.Jobs.Snapshot().OrderByDescending(j => j.CreatedAt).Take(60).ToList();
        var byId = _jobs.ToDictionary(r => r.Job.Id);
        for (var i = 0; i < snapshot.Count; i++)
        {
            var job = snapshot[i];
            if (byId.TryGetValue(job.Id, out var row))
            {
                row.Update();
                var current = _jobs.IndexOf(row);
                if (current != i)
                    _jobs.Move(current, i);
            }
            else
            {
                _jobs.Insert(i, new JobRow(job));
            }
        }
        while (_jobs.Count > snapshot.Count)
            _jobs.RemoveAt(_jobs.Count - 1);
        NoJobs.Visibility = _jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateTabHeaders()
    {
        ConvertTab.Content = TabHeader("Convert", Count(OperationCategory.Convert));
        ToolsTab.Content = TabHeader("Tools", Count(OperationCategory.Tool));
        ActionsTab.Content = TabHeader("Actions", Count(OperationCategory.Action));

        int Count(OperationCategory category) => OperationCatalog.Group(_services.Catalog.ForInputs(_files, category)).Count;
    }

    static object TabHeader(string title, int count)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = title });
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 0, 6, 1),
            Margin = new Thickness(7, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = count.ToString(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeights.Normal },
        });
        return panel;
    }

    /// <summary>Whether the "FFmpeg isn't installed" banner is showing (tests).</summary>
    internal bool IsFfmpegBannerVisible => FfmpegBanner.Visibility == Visibility.Visible;

    /// <summary>
    /// The banner shows when FFmpeg is missing and more than half of the tab's groups are unavailable because of it
    /// (their default operation's reason names FFmpeg, as the media engine's does).
    /// </summary>
    internal static bool ShouldShowFfmpegBanner(bool ffmpegMissing, IReadOnlyList<Availability> groups) =>
        ffmpegMissing && groups.Count > 0 && groups.Count(ToolStatus.NeedsFfmpeg) * 2 > groups.Count;

    void BuildTiles()
    {
        Tiles.Children.Clear();
        if (_files.Count == 0)
        {
            FfmpegBanner.Visibility = Visibility.Collapsed;
            return;
        }
        var groups = OperationCatalog.Group(_services.Catalog.ForInputs(_files, _mode.ToCategory()));
        foreach (var group in groups)
            Tiles.Children.Add(BuildTile(group));
        var missing = SafeFfmpegMissing();
        FfmpegBanner.Visibility = ShouldShowFfmpegBanner(missing, missing ? groups.Select(g => SafeAvailability(g.Default)).ToList() : [])
            ? Visibility.Visible
            : Visibility.Collapsed;

        NoOperations.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (groups.Count == 0)
        {
            var mixed = _files.Select(f => f.Kind).Distinct().Count() > 1;
            NoOperationsTitle.Text = _mode switch
            {
                WheelMode.Tools => "No tools for these files",
                WheelMode.Actions => "No actions for these files",
                _ => "Nothing to convert these files to",
            };
            NoOperationsText.Text = mixed
                ? "These files are of different kinds, and nothing here works on all of them. Try them one kind at a time."
                : "Clementine doesn't know this kind of file yet.";
        }
        TilesScroller.ScrollToTop();
    }

    FrameworkElement BuildTile(OperationGroup group)
    {
        var defaultOp = group.Default;
        var availability = SafeAvailability(defaultOp);

        var glyph = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Background = ShellTheme.Brush("Shell.AccentSoft"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = Glyphs.ForIconKey(group.IconKey),
                Style = (Style)FindResource("Shell.Glyph"),
                FontSize = 16,
                Foreground = ShellTheme.Brush("Clementine.AccentLight"),
            },
        };
        var text = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = group.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = TileSubtitle(group),
            Style = (Style)FindResource("Shell.Caption"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0),
        });
        var headerContent = new DockPanel();
        DockPanel.SetDock(glyph, Dock.Left);
        headerContent.Children.Add(glyph);
        headerContent.Children.Add(text);

        var header = new Button
        {
            Style = (Style)FindResource("TileHeader"),
            Content = headerContent,
            ToolTip = Tooltip(defaultOp, availability),
        };
        System.Windows.Automation.AutomationProperties.SetName(header, group.Name);
        System.Windows.Automation.AutomationProperties.SetHelpText(header, defaultOp.Info.Title);
        header.Click += (_, _) => Launch(defaultOp);

        var stack = new StackPanel();
        stack.Children.Add(header);
        if (group.HasVariants)
        {
            var chips = new WrapPanel { Margin = new Thickness(12, 0, 6, 8) };
            foreach (var op in group.Operations)
            {
                var chipAvailability = SafeAvailability(op);
                var chip = new Button
                {
                    Style = (Style)FindResource("Shell.ChipButton"),
                    Content = op.Info.Variant ?? op.Info.Title,
                    ToolTip = Tooltip(op, chipAvailability),
                    Opacity = chipAvailability.IsAvailable ? 1 : 0.45,
                };
                if (op == defaultOp)
                    chip.BorderBrush = ShellTheme.Brush("Clementine.Accent");
                System.Windows.Automation.AutomationProperties.SetName(chip, $"{group.Name} {op.Info.Variant ?? op.Info.Title}");
                chip.Click += (_, _) => Launch(op);
                chips.Children.Add(chip);
            }
            stack.Children.Add(chips);
        }
        else
        {
            header.Padding = new Thickness(12, 12, 12, 12);
        }

        return new Border
        {
            Style = (Style)FindResource("Tile"),
            Width = _tileWidth,
            Child = stack,
            Opacity = availability.IsAvailable ? 1 : 0.5,
        };
    }

    /// <summary>The default operation's title, or its description when the title just repeats the group name.</summary>
    static string TileSubtitle(OperationGroup group)
    {
        var info = group.Default.Info;
        static string Plain(string s) => s.Replace("…", "", StringComparison.Ordinal).Trim();
        return string.Equals(Plain(info.Title), Plain(group.Name), StringComparison.CurrentCultureIgnoreCase) && info.Description is { Length: > 0 }
            ? info.Description
            : info.Title;
    }

    /// <summary>Spreads tiles evenly over the available width (min 200 DIP each, 12 DIP gaps).</summary>
    void LayoutTiles()
    {
        const double minWidth = 200;
        const double gap = 12;
        var available = TilesScroller.ViewportWidth > 0 ? TilesScroller.ViewportWidth : TilesScroller.ActualWidth - 12;
        if (available <= 0)
            return;
        var columns = Math.Max(1, (int)Math.Floor((available + gap) / (minWidth + gap)));
        var width = Math.Floor((available - gap * columns) / columns);
        if (Math.Abs(width - _tileWidth) < 0.5)
            return;
        _tileWidth = width;
        foreach (var tile in Tiles.Children.OfType<Border>())
            tile.Width = width;
    }

    static object Tooltip(IOperation op, Availability availability)
    {
        var text = op.Info.Description ?? op.Info.Title;
        if (!availability.IsAvailable)
            text = $"{text}\n{availability.Reason}";
        return text;
    }

    Availability SafeAvailability(IOperation op)
    {
        try
        {
            return op.CheckAvailability();
        }
        catch (Exception ex)
        {
            _services.Log.Warn($"{op.Info.Id}.CheckAvailability threw", ex);
            return Availability.Unavailable("Unavailable");
        }
    }

    bool SafeFfmpegMissing()
    {
        try
        {
            return _ffmpegMissing();
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Checking for FFmpeg failed", ex);
            return false;
        }
    }

    void OnSettingsChanged()
    {
        BuildShortcutHints();
        // A new FFmpeg location changes which tiles work (and the banner); other settings don't touch the tiles.
        var ffmpegPath = _services.Settings.Current.FfmpegPath;
        if (string.Equals(ffmpegPath, _ffmpegPath, StringComparison.OrdinalIgnoreCase))
            return;
        _ffmpegPath = ffmpegPath;
        if (_files.Count > 0)
        {
            UpdateTabHeaders();
            BuildTiles();
        }
    }

    void ChooseFfmpeg()
    {
        if (ToolStatus.ChooseExecutable(this, ToolStatus.FfmpegKey) is not { } chosen)
            return;
        try
        {
            _services.Settings.Update(s => s.FfmpegPath = chosen);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Warn("Couldn't save the FFmpeg location", ex);
            MessageBox.Show(this, $"Couldn't save the settings: {ex.Message}", "Clementine", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // The tool locator refreshes on Settings.Changed; rebuild now rather than waiting for the queued handler.
        _services.Tools.Refresh();
        _ffmpegPath = chosen;
        UpdateTabHeaders();
        BuildTiles();
        if (SafeFfmpegMissing())
        {
            MessageBox.Show(this, $"Clementine still can't use FFmpeg from \"{chosen}\". It needs ffmpeg.exe with ffprobe.exe in the same folder.",
                "Clementine", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    async void Launch(IOperation op)
    {
        try
        {
            await _services.Launcher.LaunchAsync(op, _files, LaunchSource.MainWindow, ScreenGeometry.CursorPosition());
        }
        catch (Exception ex)
        {
            _services.Log.Error($"Launching {op.Info.Id} failed", ex);
            MessageBox.Show(this, $"Couldn't start {op.Info.Title}: {ex.Message}", "Clementine", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void BrowseFiles(bool add)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose files", Multiselect = true };
        if (dialog.ShowDialog(this) != true)
            return;
        var chosen = ToInputs(dialog.FileNames);
        ShowFiles(add ? [.. _files, .. chosen] : chosen);
    }

    void BrowseFolders()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose folders", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            ShowFiles(ToInputs(dialog.FolderNames));
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        // Copy only: never let Explorer treat a drop on Clementine as a move.
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        DragOverlay.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            ShowFiles(ToInputs(paths));
            Activate();
        }
    }

    List<InputFile> ToInputs(IEnumerable<string> paths)
    {
        var inputs = new List<InputFile>();
        foreach (var path in paths)
        {
            try
            {
                inputs.Add(InputFile.FromPath(path));
            }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or ArgumentException or UnauthorizedAccessException)
            {
                _services.Log.Warn($"Skipping '{path}'", ex);
            }
        }
        return inputs;
    }

    void BuildShortcutHints()
    {
        var settings = _services.Settings.Current;
        DropHint.Text = $"or hold {KeyText.Describe(settings.ConvertModifiers)} while dragging files anywhere";
        ShortcutRows.Children.Clear();
        AddHint(KeyText.Parts(settings.ConvertModifiers), "drag files", "Convert wheel");
        AddHint(KeyText.Parts(settings.ToolsModifiers), "drag files", "Tools: trim, resize, compress…");
        AddHint(KeyText.Parts(settings.ActionsModifiers), "drag files", "Actions: copy path, zip, shelf…");
        if (!string.IsNullOrWhiteSpace(settings.SelectionHotkey))
            AddHint(settings.SelectionHotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), null, "Wheel for the files selected in Explorer");

        void AddHint(IEnumerable<string> keys, string? suffix, string description)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var keysPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var key in keys)
            {
                keysPanel.Children.Add(new Border
                {
                    Style = (Style)FindResource("Shell.KeyCap"),
                    Child = new TextBlock { Text = key, FontSize = 11.5, FontWeight = FontWeights.SemiBold },
                });
            }
            if (suffix is not null)
                keysPanel.Children.Add(new TextBlock { Text = "+ " + suffix, Style = (Style)FindResource("Shell.Caption"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) });
            row.Children.Add(keysPanel);
            var label = new TextBlock { Text = description, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            ShortcutRows.Children.Add(row);
        }
    }

    static string DescribeSelection(IReadOnlyList<InputFile> files)
    {
        var parts = new List<string>();
        if (files.Count == 1)
        {
            var noun = InputSummary.KindNoun(files[0].Kind, 1);
            parts.Add(char.ToUpper(noun[0], CultureInfo.CurrentCulture) + noun[1..]);
        }
        else
        {
            var kinds = files.Select(f => f.Kind).Distinct().Count();
            if (kinds > 1)
            {
                parts.Add(string.Join(", ", files.GroupBy(f => f.Kind).OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Count()} {InputSummary.KindNoun(g.Key, g.Count())}")));
            }
            parts.Add(string.Join(", ", files.Take(4).Select(f => f.Name)) + (files.Count > 4 ? $" and {files.Count - 4} more" : ""));
        }
        var size = files.Sum(f => f.Length);
        if (size > 0)
            parts.Add(FormatSize(size));
        if (files.Count == 1)
            parts.Add(files[0].DirectoryPath);
        return string.Join("  ·  ", parts.Where(p => p.Length > 0));
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} bytes" : $"{value.ToString(value < 10 ? "0.0" : "0", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    void OnJobButton(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: JobRow row, Tag: string action })
            return;
        if (action == "cancel")
            row.Job.Cancel();
        else
            ExplorerShell.Reveal(row.Outputs, _services.Log);
    }
}
