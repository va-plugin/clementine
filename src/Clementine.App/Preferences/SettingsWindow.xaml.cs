using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Clementine.App.Branding;
using Clementine.App.Interop;
using Clementine.App.Theme;
using Clementine.App.Updates;
using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.App.Preferences;

public enum SettingsPage
{
    General,
    Wheel,
    Output,
    Performance,
    Tools,
    Explorer,
    About,
}

/// <summary>
/// Settings: General, Wheel, Output, Performance, Tools, Explorer and About pages. Changes apply immediately.
/// Registry/shortcut integrations are written only from the toggles' Click handlers, i.e. when the user flips them.
/// </summary>
public partial class SettingsWindow : Window
{
    static readonly (SettingsPage Page, string Glyph, string Title)[] PageList =
    [
        (SettingsPage.General, Glyphs.Settings, "General"),
        (SettingsPage.Wheel, Glyphs.Wheel, "Wheel"),
        (SettingsPage.Output, Glyphs.Folder, "Output"),
        (SettingsPage.Performance, Glyphs.Speed, "Performance"),
        (SettingsPage.Tools, Glyphs.Tools, "Tools"),
        (SettingsPage.Explorer, Glyphs.Explorer, "Explorer"),
        (SettingsPage.About, Glyphs.Info, "About"),
    ];

    readonly HostServices _services;
    readonly ShellRegistration _registration;
    readonly SendToShortcut _sendTo;
    readonly Updater? _updates;
    readonly Action? _restartToUpdate;
    readonly Dictionary<SettingsPage, RadioButton> _nav = [];
    IReadOnlyList<ToolInfo>? _tools;
    SettingsPage _page;
    bool _detectingTools;
    TextBlock? _hotkeyProblem;
    Action<UpdateStatus>? _showUpdateStatus;

    /// <param name="updates">The app's updater (null: no Updates card on the About page, e.g. in tests).</param>
    /// <param name="restartToUpdate">What "Restart to update" does (the shell's restart, which drains jobs first).</param>
    public SettingsWindow(HostServices services, ShellRegistration registration, SendToShortcut sendTo, IReadOnlyList<ToolInfo>? tools = null,
        Updater? updates = null, Action? restartToUpdate = null)
    {
        InitializeComponent();
        ShellTheme.ApplyWindow(this);
        _services = services;
        _registration = registration;
        _sendTo = sendTo;
        _tools = tools;
        _updates = updates;
        _restartToUpdate = restartToUpdate;
        Logo.Source = AppIcon.Logo;
        if (_updates is not null)
        {
            _updates.StatusChanged += OnUpdateStatusChanged;
            Closed += (_, _) => _updates.StatusChanged -= OnUpdateStatusChanged;
        }

        foreach (var (page, glyph, title) in PageList)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("Shell.Glyph"), FontSize = 15, Width = 20, Margin = new Thickness(0, 0, 12, 0) });
            content.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
            var button = new RadioButton { Style = (Style)FindResource("Shell.NavItem"), Content = content, GroupName = "nav" };
            AutomationName(button, title);
            button.Checked += (_, _) => Render(page);
            _nav[page] = button;
            Nav.Children.Add(button);
        }
        ShowPage(SettingsPage.General);
    }

    public SettingsPage CurrentPage => _page;

    /// <summary>Scrolls the page to its end (snapshots of long pages).</summary>
    internal void ScrollPageToEnd()
    {
        PageScroller.UpdateLayout();
        PageScroller.ScrollToEnd();
        PageScroller.UpdateLayout();
    }

    public void ShowPage(SettingsPage page)
    {
        if (_nav[page].IsChecked == true)
            Render(page);
        else
            _nav[page].IsChecked = true;
    }

    void Render(SettingsPage page)
    {
        _page = page;
        _hotkeyProblem = null; // the Wheel page sets it again while it is built
        _showUpdateStatus = null; // so does the About page for the update status
        ShownUpdateStatus = null;
        PageHost.Children.Clear();
        var content = page switch
        {
            SettingsPage.General => GeneralPage(),
            SettingsPage.Wheel => WheelPage(),
            SettingsPage.Output => OutputPage(),
            SettingsPage.Performance => PerformancePage(),
            SettingsPage.Tools => ToolsPage(),
            SettingsPage.Explorer => ExplorerPage(),
            _ => AboutPage(),
        };
        foreach (var element in content)
            PageHost.Children.Add(element);
        PageScroller.ScrollToTop();
    }

    // ===== Pages =====

    IEnumerable<FrameworkElement> GeneralPage()
    {
        var s = _services.Settings.Current;
        yield return Header("General", "How Clementine starts and talks to you.");
        yield return Card(
            Row("Start with Windows", "Runs quietly in the tray when you sign in, so the wheel is always ready.",
                Toggle(SafeRead(_registration.IsLaunchAtLoginEnabled, s.LaunchAtLogin), on =>
                {
                    _registration.SetLaunchAtLogin(on);
                    Save(x => x.LaunchAtLogin = on);
                })),
            Row("Sounds", "A soft sound when a job finishes.", Toggle(s.PlaySounds, on => Save(x => x.PlaySounds = on))),
            Row("Notifications", "Progress and result cards in the corner. Failures and text results always show.",
                Toggle(s.ShowNotifications, on => Save(x => x.ShowNotifications = on))));

        yield return Section("Updates");
        var updateNote = _updates is { IsAvailable: false }
            ? "Installed copies look for a new version on GitHub every few hours. " + _updates.Status.Text
            : "Looks for a new version on GitHub a minute after Clementine starts and every 6 hours, and downloads it quietly. You choose when to restart.";
        yield return Card(Row("Check for updates automatically", updateNote,
            Toggle(s.CheckForUpdates, on => Save(x => x.CheckForUpdates = on))));

        var count = s.RememberedOptions.Count;
        var forget = new Button { Content = "Forget", IsEnabled = count > 0, MinWidth = 84 };
        forget.Click += (_, _) =>
        {
            Save(x => x.RememberedOptions.Clear());
            Render(SettingsPage.General);
        };
        yield return Section("Dialogs");
        yield return Card(Row("Remembered choices",
            count == 0 ? "Option dialogs reopen with the values you used last time. Nothing remembered yet."
                : $"Option dialogs reopen with the values you used last time ({count} {(count == 1 ? "operation" : "operations")}).",
            forget));
    }

    IEnumerable<FrameworkElement> WheelPage()
    {
        var s = _services.Settings.Current;
        yield return Header("Wheel", "Hold these keys while dragging files to open a wheel under the pointer.");
        yield return Card(Row("Show the wheel while dragging", "Turn off to keep Clementine in the tray without watching drags.",
            Toggle(s.WheelEnabled, on => Save(x => x.WheelEnabled = on, applyWheel: true))));

        var problems = new TextBlock { Style = (Style)FindResource("Shell.Caption"), Foreground = ShellTheme.Brush("Clementine.Error"), Margin = new Thickness(4, 10, 0, 0), Visibility = Visibility.Collapsed };
        var current = new Dictionary<WheelMode, TriggerModifiers>
        {
            [WheelMode.Convert] = s.ConvertModifiers,
            [WheelMode.Tools] = s.ToolsModifiers,
            [WheelMode.Actions] = s.ActionsModifiers,
        };

        FrameworkElement Keys(WheelMode mode)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (flag, label) in new[] { (TriggerModifiers.Control, "Ctrl"), (TriggerModifiers.Alt, "Alt"), (TriggerModifiers.Shift, "Shift"), (TriggerModifiers.Windows, "Win") })
            {
                var toggle = new ToggleButton { Content = label, Style = (Style)FindResource("Shell.KeyToggle"), IsChecked = current[mode].HasFlag(flag) };
                AutomationName(toggle, $"{mode} wheel {label}");
                toggle.Click += (_, _) =>
                {
                    current[mode] = toggle.IsChecked == true ? current[mode] | flag : current[mode] & ~flag;
                    var found = ModifierConflicts.Find(current[WheelMode.Convert], current[WheelMode.Tools], current[WheelMode.Actions]);
                    problems.Text = found.Count == 0 ? "" : "Not saved yet: " + string.Join(" ", found);
                    problems.Visibility = found.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                    if (found.Count == 0)
                    {
                        Save(x =>
                        {
                            x.ConvertModifiers = current[WheelMode.Convert];
                            x.ToolsModifiers = current[WheelMode.Tools];
                            x.ActionsModifiers = current[WheelMode.Actions];
                        }, applyWheel: true);
                    }
                };
                panel.Children.Add(toggle);
            }
            return panel;
        }

        yield return Section("Keys");
        var card = Card(
            Row("Convert", "Formats: MP4, ProRes, HAP, PNG, PDF…", Keys(WheelMode.Convert)),
            Row("Tools", "Trim, resize, compress, rotate…", Keys(WheelMode.Tools)),
            Row("Actions", "Copy path, zip, checksum, shelf…", Keys(WheelMode.Actions)));
        var keysBlock = new StackPanel();
        keysBlock.Children.Add(card);
        keysBlock.Children.Add(problems);
        yield return keysBlock;

        yield return Section("Selected files");
        // Why the hotkey isn't active (usually: another app already registered it), straight from the running wheel.
        var hotkeyProblem = new TextBlock
        {
            Style = (Style)FindResource("Shell.Caption"),
            Foreground = ShellTheme.Brush("Shell.Warning"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(hotkeyProblem, System.Windows.Automation.AutomationLiveSetting.Polite);
        _hotkeyProblem = hotkeyProblem;
        RefreshHotkeyProblem();
        var hotkeyBlock = new StackPanel();
        hotkeyBlock.Children.Add(Card(Row("Wheel for Explorer's selection", "Press this anywhere to open the wheel for the files selected in Explorer.",
            HotkeyRecorder(s.SelectionHotkey))));
        hotkeyBlock.Children.Add(hotkeyProblem);
        yield return hotkeyBlock;
    }

    /// <summary>The hotkey warning under the recorder (tests): its text, or null when hidden.</summary>
    internal string? ShownHotkeyProblem => _hotkeyProblem is { Visibility: Visibility.Visible } shown ? shown.Text : null;

    /// <summary>Re-reads <see cref="IWheelPresenter.HotkeyProblem"/> into the warning on the Wheel page (when it is showing).</summary>
    void RefreshHotkeyProblem()
    {
        if (_hotkeyProblem is not { } warning)
            return;
        string? problem;
        try
        {
            problem = _services.Wheel?.HotkeyProblem;
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Reading the hotkey state failed", ex);
            problem = null;
        }
        warning.Text = problem ?? "";
        warning.Visibility = string.IsNullOrWhiteSpace(problem) ? Visibility.Collapsed : Visibility.Visible;
    }

    IEnumerable<FrameworkElement> OutputPage()
    {
        var s = _services.Settings.Current;
        yield return Header("Output", "Where converted files go. Existing files are never overwritten: Clementine adds (2), (3)… instead.");

        var nextTo = new RadioButton { Content = "Next to the original file", GroupName = "output", IsChecked = s.OutputMode == OutputLocationMode.NextToOriginal, Margin = new Thickness(0, 0, 0, 12) };
        var custom = new RadioButton { Content = "Always in this folder:", GroupName = "output", IsChecked = s.OutputMode == OutputLocationMode.CustomFolder };
        var path = new TextBox { Text = s.CustomOutputFolder ?? "", IsReadOnly = true, Tag = "No folder chosen", Margin = new Thickness(30, 10, 8, 0) };
        var browse = new Button { Content = "Browse…", Margin = new Thickness(0, 10, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        AutomationName(path, "Output folder");
        AutomationName(browse, "Choose the output folder");

        bool ChooseFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Save converted files in", InitialDirectory = Directory.Exists(path.Text) ? path.Text : "" };
            if (dialog.ShowDialog(this) != true)
                return false;
            path.Text = dialog.FolderName;
            Save(x =>
            {
                x.CustomOutputFolder = dialog.FolderName;
                x.OutputMode = OutputLocationMode.CustomFolder;
            });
            custom.IsChecked = true;
            return true;
        }

        nextTo.Click += (_, _) => Save(x => x.OutputMode = OutputLocationMode.NextToOriginal);
        custom.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(path.Text) && !ChooseFolder())
            {
                nextTo.IsChecked = true;
                return;
            }
            Save(x => x.OutputMode = OutputLocationMode.CustomFolder);
        };
        browse.Click += (_, _) => ChooseFolder();

        var pathRow = new Grid();
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.Children.Add(path);
        Grid.SetColumn(browse, 1);
        pathRow.Children.Add(browse);

        var choices = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        choices.Children.Add(nextTo);
        choices.Children.Add(custom);
        choices.Children.Add(pathRow);
        yield return Card(Wide("Save results", null, choices));

        yield return Card(Row("Show in Explorer when done", "Opens the folder with the new file selected after each job.",
            Toggle(s.RevealInExplorerWhenDone, on => Save(x => x.RevealInExplorerWhenDone = on))));
    }

    IEnumerable<FrameworkElement> PerformancePage()
    {
        var s = _services.Settings.Current;
        yield return Header("Performance", "How hard Clementine works your machine.");

        var value = new TextBlock { Width = 28, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        var slider = new Slider { Minimum = 1, Maximum = 8, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 200, Value = Math.Clamp(s.MaxConcurrentHeavyJobs, 1, 8), VerticalAlignment = VerticalAlignment.Center };
        AutomationName(slider, "Heavy jobs at once");
        value.Text = ((int)slider.Value).ToString(CultureInfo.CurrentCulture);
        slider.ValueChanged += (_, e) =>
        {
            value.Text = ((int)e.NewValue).ToString(CultureInfo.CurrentCulture);
            Save(x => x.MaxConcurrentHeavyJobs = (int)e.NewValue);
        };
        var sliderRow = new StackPanel { Orientation = Orientation.Horizontal };
        sliderRow.Children.Add(slider);
        sliderRow.Children.Add(value);

        yield return Card(
            Row("Heavy jobs at once", "Video encodes and document conversions running side by side.", sliderRow),
            Row("Hardware encoding", "Use the graphics card (NVENC, Quick Sync, AMF) for H.264 and H.265 when it works.",
                Toggle(s.UseHardwareEncoding, on => Save(x => x.UseHardwareEncoding = on))));

        yield return Section("VJ codecs (DXV, HAP, ProRes)");
        var rates = new ComboBox { Width = 140, ItemsSource = new[] { 44100, 48000, 96000 }, SelectedItem = s.VjAudioSampleRate };
        if (rates.SelectedItem is null)
            rates.SelectedItem = 48000;
        rates.ItemTemplate = null;
        rates.ItemStringFormat = "{0:N0} Hz";
        AutomationName(rates, "Audio sample rate");
        rates.SelectionChanged += (_, _) =>
        {
            if (rates.SelectedItem is int rate)
                Save(x => x.VjAudioSampleRate = rate);
        };
        yield return Card(
            Row("Audio sample rate", "PCM audio in DXV, HAP and ProRes files. Resolume expects 48 kHz.", rates),
            Row("Constant frame rate", "Turns variable-frame-rate phone clips into smooth constant-rate VJ loops.",
                Toggle(s.ForceConstantFrameRate, on => Save(x => x.ForceConstantFrameRate = on))));
    }

    IEnumerable<FrameworkElement> ToolsPage()
    {
        var refresh = new Button { Content = "Check again", HorizontalAlignment = HorizontalAlignment.Right };
        refresh.Click += async (_, _) => await LoadToolsAsync(force: true);
        var header = Header("Tools", "Programs Clementine runs for some conversions. FFmpeg and 7-Zip are built in, Edge is part of Windows and LibreOffice is optional. Choose… uses another copy instead.");
        var top = new Grid();
        top.Children.Add(header);
        top.Children.Add(refresh);
        refresh.VerticalAlignment = VerticalAlignment.Top;
        yield return top;

        if (_tools is null)
        {
            yield return Card(Wide("Looking for tools…", "This takes a moment.", null));
            _ = LoadToolsAsync(force: false);
            yield break;
        }

        var s = _services.Settings.Current;
        foreach (var tool in _tools)
        {
            string? overridePath = tool.Key switch
            {
                ToolStatus.FfmpegKey => s.FfmpegPath,
                ToolStatus.LibreOfficeKey => s.LibreOfficePath,
                ToolStatus.SevenZipKey => s.SevenZipPath,
                _ => null,
            };
            yield return Card(ToolRow(tool, overridePath));
        }
    }

    IEnumerable<FrameworkElement> ExplorerPage()
    {
        yield return Header("Explorer", "Reach Clementine from Explorer without dragging.");
        yield return Card(
            Row("Right-click menu", "Adds \"Convert with Clementine\" for files and folders. On Windows 11 it's under \"Show more options\".",
                Toggle(SafeRead(_registration.IsContextMenuInstalled, _services.Settings.Current.ExplorerContextMenu), on =>
                {
                    _registration.SetContextMenu(on);
                    Save(x => x.ExplorerContextMenu = on);
                })),
            Row("Send to", "Adds Clementine to the \"Send to\" menu.",
                Toggle(SafeRead(() => _sendTo.Exists, _services.Settings.Current.SendToShortcut), on =>
                {
                    if (on)
                        _sendTo.Create();
                    else
                        _sendTo.Remove();
                    Save(x => x.SendToShortcut = on);
                })));
        yield return Note("Both open the wheel at the pointer for the chosen files. Turning them off removes them again.");
    }

    IEnumerable<FrameworkElement> AboutPage()
    {
        var hero = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 22) };
        hero.Children.Add(new Image { Source = AppIcon.Logo, Width = 72, Height = 72, Margin = new Thickness(0, 0, 18, 0) });
        var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heroText.Children.Add(new TextBlock { Text = "Clementine", Style = (Style)FindResource("Shell.Title"), FontSize = 26 });
        heroText.Children.Add(new TextBlock { Text = "A Visual Artform product", Style = (Style)FindResource("Shell.Body"), Foreground = (Brush)FindResource("Clementine.Accent"), Margin = new Thickness(0, 2, 0, 0) });
        heroText.Children.Add(new TextBlock { Text = $"Version {AppVersion} · © {DateTime.Now.Year} Visual Artform", Style = (Style)FindResource("Shell.Caption"), Margin = new Thickness(0, 2, 0, 0) });
        heroText.Children.Add(new TextBlock { Text = "Drag-and-drop file conversion for Windows. Everything runs on this PC.", Style = (Style)FindResource("Shell.Body"), Margin = new Thickness(0, 6, 0, 0) });
        var openSource = new TextBlock { Style = (Style)FindResource("Shell.Body"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        openSource.Inlines.Add(Licences.OpenSourceLine + " · ");
        openSource.Inlines.Add(Link(Licences.ProjectLinkText, UpdateSetup.ProjectUrl, "Clementine on GitHub"));
        heroText.Children.Add(openSource);
        hero.Children.Add(heroText);
        yield return hero;

        if (_updates is not null)
            yield return UpdatesCard(_updates);

        var logs = new Button { Content = "Open log folder", Margin = new Thickness(0, 0, 8, 0) };
        logs.Click += (_, _) => OpenFolder(AppPaths.LogDirectory);
        var data = new Button { Content = "Open settings folder" };
        data.Click += (_, _) => OpenFolder(AppPaths.DataDirectory);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(logs);
        buttons.Children.Add(data);
        var files = new StackPanel();
        files.Children.Add(new TextBlock
        {
            Text = AppPaths.DataDirectory,
            Style = (Style)FindResource("Shell.Caption"),
            FontFamily = (FontFamily)FindResource("Shell.MonoFont"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        files.Children.Add(buttons);
        yield return Card(Wide("Settings and logs", null, files));

        yield return Section("Programs included with Clementine");
        var programs = new StackPanel();
        foreach (var component in Licences.BundledPrograms)
            programs.Children.Add(LicenceRow(component));
        yield return Card(Wide(null, null, programs));

        yield return Section("Open-source libraries inside Clementine");
        var bundled = new StackPanel();
        foreach (var component in Licences.BundledComponents())
            bundled.Children.Add(LicenceRow(component));
        yield return Card(Wide(null, null, bundled));

        yield return Section("Programs Clementine uses when they're on this PC (not included)");
        var external = new StackPanel();
        foreach (var component in Licences.ExternalPrograms)
            external.Children.Add(LicenceRow(component));
        yield return Card(Wide(null, null, external));

        var notices = new Button { Content = "Third-party notices", Margin = new Thickness(0, 4, 8, 0) };
        notices.Click += (_, _) => ShowNotices();
        var licenceFolder = new Button { Content = "Open licenses folder", Margin = new Thickness(0, 4, 0, 0) };
        licenceFolder.Click += (_, _) => OpenFolder(Path.Combine(AppContext.BaseDirectory, Licences.LicencesFolderName));
        var noticeButtons = new StackPanel { Orientation = Orientation.Horizontal };
        noticeButtons.Children.Add(notices);
        noticeButtons.Children.Add(licenceFolder);
        yield return noticeButtons;
        yield return Note(Licences.NoticesNote);
    }

    /// <summary>The About page's Updates card: status, "Check for updates" and, once one is downloaded, "Restart to update".</summary>
    FrameworkElement UpdatesCard(Updater updates)
    {
        var status = new TextBlock { Style = (Style)FindResource("Shell.Caption"), FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(status, System.Windows.Automation.AutomationLiveSetting.Polite);
        var check = new Button { Content = "Check for updates", Margin = new Thickness(0, 0, 8, 0) };
        var restart = new Button { Content = "Restart to update", Style = (Style)FindResource("Shell.PrimaryButton") };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            ShowUpdateStatus(await updates.CheckNowAsync());
        };
        restart.Click += (_, _) => _restartToUpdate?.Invoke();

        void Show(UpdateStatus current)
        {
            ShownUpdateStatus = status.Text = current.Stage == UpdateStage.Idle
                ? _services.Settings.Current.CheckForUpdates
                    ? "Clementine checks a minute after it starts and every 6 hours."
                    : "Automatic checks are off (Settings › General). Check whenever you like."
                : current.Text;
            status.Foreground = ShellTheme.Brush(current.Stage switch
            {
                UpdateStage.Ready or UpdateStage.Restarting => "Clementine.AccentLight",
                UpdateStage.UpToDate => "Clementine.Success",
                _ => "Clementine.TextSecondary",
            });
            // Once a restart has handed the update to the installer, nothing more can be checked or restarted.
            check.IsEnabled = current.Stage is not (UpdateStage.Unavailable or UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Restarting);
            restart.Visibility = current.Stage == UpdateStage.Ready && _restartToUpdate is not null ? Visibility.Visible : Visibility.Collapsed;
        }

        _showUpdateStatus = Show;
        Show(updates.Status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(check);
        buttons.Children.Add(restart);
        var content = new StackPanel();
        content.Children.Add(status);
        content.Children.Add(buttons);
        if (updates.IsAvailable)
            content.Children.Add(new TextBlock
            {
                Text = "Updates come from Clementine's releases on GitHub. Nothing else is sent, and nothing installs until you restart.",
                Style = (Style)FindResource("Shell.Caption"),
                FontSize = 11.5,
                Margin = new Thickness(0, 10, 0, 0),
            });
        return Card(Wide("Updates", null, content));
    }

    void OnUpdateStatusChanged(object? sender, UpdateStatus status) =>
        Dispatcher.BeginInvoke(() => ShowUpdateStatus(status));

    void ShowUpdateStatus(UpdateStatus status) => _showUpdateStatus?.Invoke(status);

    /// <summary>The update status line on the About page (tests): its text, or null when the page isn't showing.</summary>
    internal string? ShownUpdateStatus { get; private set; }

    System.Windows.Documents.Hyperlink Link(string text, string url, string automationName)
    {
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text)) { ToolTip = url };
        System.Windows.Automation.AutomationProperties.SetName(link, automationName);
        link.Click += (_, _) => ExplorerShell.OpenUrl(url, _services.Log);
        return link;
    }

    // ===== Building blocks =====

    FrameworkElement Header(string title, string subtitle)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Shell.Title") });
        panel.Children.Add(new TextBlock { Text = subtitle, Style = (Style)FindResource("Shell.Caption"), FontSize = 13, Margin = new Thickness(0, 4, 120, 0) });
        return panel;
    }

    FrameworkElement Section(string title) =>
        new TextBlock { Text = title, Style = (Style)FindResource("Shell.SectionLabel"), Margin = new Thickness(4, 16, 0, 8) };

    FrameworkElement Note(string text) =>
        new TextBlock { Text = text, Style = (Style)FindResource("Shell.Caption"), Margin = new Thickness(4, 10, 0, 0) };

    FrameworkElement Card(params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0)
                stack.Children.Add(new Border { Height = 1, Background = ShellTheme.Brush("Shell.Divider"), Margin = new Thickness(-16, 0, -16, 0) });
            stack.Children.Add(rows[i]);
        }
        return new Border { Style = (Style)FindResource("Shell.CardBorder"), Child = stack, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(16, 2, 16, 2) };
    }

    FrameworkElement Row(string title, string? description, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 12), MinHeight = 36 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13.5 });
        if (description is not null)
            text.Children.Add(new TextBlock { Text = description, Style = (Style)FindResource("Shell.Caption"), Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(text);
        // Toggles, sliders and pickers have no text of their own: screen readers announce the row's title instead.
        var hasOwnText = control is ContentControl { Content: string { Length: > 0 } };
        if (!hasOwnText && string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(control)))
            AutomationName(control, title);
        if (description is not null && string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetHelpText(control)))
            System.Windows.Automation.AutomationProperties.SetHelpText(control, description);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    FrameworkElement Wide(string? title, string? description, FrameworkElement? content)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
        if (title is not null)
            panel.Children.Add(new TextBlock { Text = title, FontSize = 13.5, Margin = new Thickness(0, 0, 0, 2) });
        if (description is not null)
            panel.Children.Add(new TextBlock { Text = description, Style = (Style)FindResource("Shell.Caption") });
        if (content is not null)
        {
            if (title is not null || description is not null)
                content.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(content);
        }
        return panel;
    }

    CheckBox Toggle(bool value, Action<bool> onUserChange)
    {
        var toggle = new CheckBox { Style = (Style)FindResource("Shell.Toggle"), IsChecked = value };
        // Click fires only for user interaction, never when the page is built or redrawn.
        toggle.Click += (_, _) =>
        {
            var on = toggle.IsChecked == true;
            try
            {
                onUserChange(on);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException or System.Runtime.InteropServices.COMException)
            {
                toggle.IsChecked = !on;
                _services.Log.Warn("Settings change failed", ex);
                MessageBox.Show(this, $"Windows didn't allow that change: {ex.Message}", "Clementine", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        return toggle;
    }

    FrameworkElement HotkeyRecorder(string current)
    {
        var button = new Button { MinWidth = 170, HorizontalContentAlignment = HorizontalAlignment.Center };
        AutomationName(button, "Selection hotkey");
        var recording = false;

        void ShowValue(string? hotkey)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            if (string.IsNullOrWhiteSpace(hotkey))
            {
                panel.Children.Add(new TextBlock { Text = "Off — click to set", Foreground = ShellTheme.Brush("Clementine.TextSecondary") });
            }
            else
            {
                foreach (var part in hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    panel.Children.Add(new Border { Style = (Style)FindResource("Shell.KeyCap"), Child = new TextBlock { Text = part, FontSize = 11.5, FontWeight = FontWeights.SemiBold } });
            }
            button.Content = panel;
        }

        ShowValue(current);
        button.Click += (_, _) =>
        {
            recording = true;
            button.Content = new TextBlock { Text = "Press keys… (Esc cancels, Backspace turns off)", Foreground = ShellTheme.Brush("Clementine.AccentLight"), FontSize = 12 };
            button.Focus();
        };
        button.LostKeyboardFocus += (_, _) =>
        {
            if (!recording)
                return;
            recording = false;
            ShowValue(_services.Settings.Current.SelectionHotkey);
        };
        button.PreviewKeyDown += (_, e) =>
        {
            if (!recording)
                return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                recording = false;
                ShowValue(_services.Settings.Current.SelectionHotkey);
                return;
            }
            if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                recording = false;
                Save(x => x.SelectionHotkey = "", applyWheel: true);
                ShowValue("");
                return;
            }
            if (KeyText.FormatHotkey(Keyboard.Modifiers, key) is { } hotkey)
            {
                recording = false;
                Save(x => x.SelectionHotkey = hotkey, applyWheel: true);
                ShowValue(hotkey);
            }
        };
        return button;
    }

    FrameworkElement ToolRow(ToolInfo tool, string? overridePath)
    {
        var grid = new Grid { Margin = new Thickness(0, 14, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var state = ToolStatus.StateOf(tool);
        var (badgeBrush, badgeGlyph, glyphBrush) = state switch
        {
            ToolState.Missing => ("Shell.ErrorSoft", Glyphs.Cancel, "Clementine.Error"),
            ToolState.NotInstalledOptional => ("Shell.CardHover", Glyphs.Add, "Clementine.TextSecondary"),
            _ => ("Shell.LeafSoft", Glyphs.Check, "Clementine.Success"),
        };
        var badge = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = ShellTheme.Brush(badgeBrush),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 14, 0),
            Child = new TextBlock
            {
                Text = badgeGlyph,
                Style = (Style)FindResource("Shell.Glyph"),
                FontSize = 13,
                Foreground = ShellTheme.Brush(glyphBrush),
            },
        };
        grid.Children.Add(badge);

        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
        title.Inlines.Add(tool.Name);
        // A built-in tool names its version on the status line ("Built in · FFmpeg 8.1.2").
        if (tool.Version is not null && state != ToolState.BuiltIn)
            title.Inlines.Add(new System.Windows.Documents.Run("  " + tool.Version) { FontWeight = FontWeights.Normal, Foreground = ShellTheme.Brush("Clementine.TextSecondary"), FontSize = 12.5 });
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = tool.Purpose, Style = (Style)FindResource("Shell.Caption"), Margin = new Thickness(0, 2, 0, 0) });
        var showsPath = state is ToolState.Installed;
        text.Children.Add(new TextBlock
        {
            Text = ToolStatus.StatusLine(tool),
            Style = (Style)FindResource("Shell.Caption"),
            FontFamily = showsPath ? (FontFamily)FindResource("Shell.MonoFont") : (FontFamily)FindResource("Clementine.Font"),
            FontSize = showsPath ? 11.5 : 12,
            FontWeight = state == ToolState.BuiltIn ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = ShellTheme.Brush(state switch
            {
                ToolState.Missing => "Clementine.Error",
                ToolState.BuiltIn => "Clementine.Success",
                _ => "Clementine.TextSecondary",
            }),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = tool.Path,
            Margin = new Thickness(0, 4, 0, 0),
        });
        if (!string.IsNullOrWhiteSpace(overridePath))
            text.Children.Add(new TextBlock { Text = "Using the location you chose.", Style = (Style)FindResource("Shell.Caption"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (!tool.IsInstalled)
        {
            // Optional tools get a plain button: nothing is wrong when they're missing.
            var get = new Button { Content = $"Get {tool.Name}", Margin = new Thickness(0, 0, 8, 0) };
            if (state == ToolState.Missing)
                get.Style = (Style)FindResource("Shell.PrimaryButton");
            get.Click += (_, _) => ExplorerShell.OpenUrl(tool.DownloadUrl, _services.Log);
            buttons.Children.Add(get);
        }
        if (tool.CanOverride)
        {
            var change = new Button { Content = "Choose…" };
            change.Click += async (_, _) =>
            {
                if (ToolStatus.ChooseExecutable(this, tool.Key) is not { } chosen)
                    return;
                SetOverride(tool.Key, chosen);
                await LoadToolsAsync(force: true);
            };
            buttons.Children.Add(change);
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                var reset = new Button { Content = "Automatic", Style = (Style)FindResource("Shell.SubtleButton"), Margin = new Thickness(6, 0, 0, 0) };
                reset.Click += async (_, _) =>
                {
                    SetOverride(tool.Key, null);
                    await LoadToolsAsync(force: true);
                };
                buttons.Children.Add(reset);
            }
        }
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        return grid;
    }

    FrameworkElement LicenceRow(ComponentLicence component)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var name = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 16, 0), ToolTip = component.Url ?? component.Name };
        if (component.Url is { } url)
        {
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(component.Name)) { ToolTip = url };
            System.Windows.Automation.AutomationProperties.SetName(link, $"{component.Name} website");
            link.Click += (_, _) => ExplorerShell.OpenUrl(url, _services.Log);
            name.Inlines.Add(link);
        }
        else
        {
            name.Text = component.Name;
        }
        grid.Children.Add(name);
        var version = new TextBlock { Text = component.Version, Style = (Style)FindResource("Shell.Caption"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(version, 1);
        grid.Children.Add(version);
        var licence = new TextBlock { Text = component.Licence, Style = (Style)FindResource("Shell.Caption"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = component.Licence };
        Grid.SetColumn(licence, 2);
        grid.Children.Add(licence);
        if (!string.IsNullOrEmpty(component.Note))
        {
            var note = new TextBlock { Text = component.Note, Style = (Style)FindResource("Shell.Caption"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(note, 1);
            Grid.SetColumnSpan(note, 3);
            grid.Children.Add(note);
        }
        return grid;
    }

    void ShowNotices()
    {
        try
        {
            if (Licences.NoticesFile() is not { } path)
            {
                MessageBox.Show(this, $"{Licences.NoticesFileName} is missing from Clementine's folder.", "Clementine", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Dialogs.TextViewerWindow.Open("Third-party notices", Licences.NoticesFileName, File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Warn("Couldn't open the third-party notices", ex);
            MessageBox.Show(this, ex.Message, "Clementine", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ===== Actions =====

    /// <summary>Detects tools in the background and redraws the Tools page when it is showing.</summary>
    public async Task LoadToolsAsync(bool force)
    {
        if (_detectingTools || (_tools is not null && !force))
            return;
        _detectingTools = true;
        try
        {
            _tools = await ToolStatus.DetectAsync(_services.Tools);
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Tool detection failed", ex);
            _tools = [];
        }
        finally
        {
            _detectingTools = false;
        }
        if (_page == SettingsPage.Tools && IsLoaded)
            Render(SettingsPage.Tools);
    }

    void SetOverride(string key, string? path) => Save(x =>
    {
        switch (key)
        {
            case ToolStatus.FfmpegKey: x.FfmpegPath = path; break;
            case ToolStatus.LibreOfficeKey: x.LibreOfficePath = path; break;
            case ToolStatus.SevenZipKey: x.SevenZipPath = path; break;
        }
    });

    void Save(Action<ClementineSettings> change, bool applyWheel = false)
    {
        try
        {
            _services.Settings.Update(change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Warn("Couldn't save settings", ex);
            MessageBox.Show(this, $"Couldn't save the settings: {ex.Message}", "Clementine", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (applyWheel)
        {
            try
            {
                _services.Wheel?.ApplySettings();
            }
            catch (Exception ex)
            {
                _services.Log.Warn("Wheel.ApplySettings failed", ex);
            }
            // The wheel re-registers the hotkey now (on this thread) and again from its own Settings.Changed handler,
            // which is queued: read the outcome now and once more after that has run.
            RefreshHotkeyProblem();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, RefreshHotkeyProblem);
        }
    }

    bool SafeRead(Func<bool> read, bool fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _services.Log.Warn("Couldn't read integration state", ex);
            return fallback;
        }
    }

    void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            ExplorerShell.Open(path, _services.Log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationException)
        {
            MessageBox.Show(this, ex.Message, "Clementine", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    static string AppVersion
    {
        get
        {
            var informational = typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = informational ?? typeof(SettingsWindow).Assembly.GetName().Version?.ToString() ?? "0.1";
            var plus = version.IndexOf('+');
            return plus > 0 ? version[..plus] : version;
        }
    }
}
