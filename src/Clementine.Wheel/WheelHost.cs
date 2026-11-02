using System.IO;
using System.Windows;
using System.Windows.Threading;
using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.Wheel;

/// <summary>
/// Global drag monitor + radial wheel. Construct and use on the WPF UI thread; calls from other threads are
/// marshalled to it.
/// <para>
/// Shift-drag flow: the <see cref="DragMonitor"/> hook notices a drag with matching modifiers → an almost invisible
/// <see cref="CatcherWindow"/> follows the cursor → when an OLE drag with files enters it, the drag
/// <see cref="WheelWindow"/> opens there → the drop chooses an operation, which goes to
/// <see cref="HostServices.Launcher"/>. The selection hotkey and <see cref="ShowForFiles"/> open the click-mode wheel.
/// </para>
/// </summary>
public sealed class WheelHost : IWheelPresenter
{
    static readonly TimeSpan ReleaseGrace = TimeSpan.FromMilliseconds(150);
    static readonly TimeSpan CatcherWatchInterval = TimeSpan.FromMilliseconds(100);

    readonly HostServices _services;
    readonly Dispatcher _dispatcher;
    readonly DispatcherTimer _releaseTimer;
    readonly DispatcherTimer _catcherWatch;
    readonly ReleaseWatch _catcherRelease = new(2);

    DragMonitor? _monitor;
    CatcherWindow? _catcher;
    WheelWindow? _dragWheel;
    WheelWindow? _clickWheel;
    HotkeyManager? _hotkey;
    ModifierBindings _bindings = ModifierBindings.Default;
    bool _started;
    // Per-drag decisions, keyed by DragSignal.Drag: a drag whose catcher must stay hidden (foreign data, a source
    // Windows won't let drop on us) and the last drag whose source was checked.
    int _currentDrag = -1;
    int _suppressedDrag = -1;
    int _checkedDrag = -1;
    string? _lastHiddenReason;
    WheelMode _armedMode = WheelMode.Convert;
    IReadOnlyList<InputFile> _dragFiles = [];
    IReadOnlyList<InputFile> _clickFiles = [];
    LaunchSource _clickSource = LaunchSource.Wheel;

    public WheelHost(HostServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _releaseTimer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher) { Interval = ReleaseGrace };
        _releaseTimer.Tick += (_, _) => Guard("release", OnReleaseTimer);
        _catcherWatch = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = CatcherWatchInterval };
        _catcherWatch.Tick += (_, _) => Guard("catcher watchdog", OnCatcherWatch);
    }

    ILog Log => _services.Log;

    /// <summary>Whether the logical primary button is held (tests replace it).</summary>
    internal Func<bool> IsButtonDown { get; set; } = NativeMethods.IsPrimaryButtonDown;

    /// <summary>Why the catcher must stay hidden for an armed drag (source at its origin, catcher at its point), or null (tests replace it).</summary>
    internal Func<DragSignal, string?> CatcherBlocker { get; set; } = signal => DragSource.ReasonToStayHidden(signal.Origin, signal.Point);

    /// <summary>The catcher window, once created (tests).</summary>
    internal CatcherWindow? CatcherWindow => _catcher;

    /// <summary>True while the global drag monitor's hook is installed.</summary>
    internal bool IsMonitoring => _monitor?.IsRunning == true;

    /// <summary>The drag-mode wheel window, once created (end-to-end tests).</summary>
    internal WheelWindow? DragWheelWindow => _dragWheel;

    /// <summary>The registered selection hotkey, if any.</summary>
    internal HotkeyGesture? RegisteredHotkey => _hotkey?.Registered;

    public string? HotkeyProblem => _hotkey?.Problem;

    public void Start() => OnUi("start", () =>
    {
        if (_started)
            return;
        _started = true;
        _services.Settings.Changed += OnSettingsChanged;
        ApplySettingsCore();
        WarmUp();
    });

    public void Stop() => OnUi("stop", () =>
    {
        _started = false;
        _services.Settings.Changed -= OnSettingsChanged;
        _releaseTimer.Stop();
        _catcherWatch.Stop();
        _monitor?.Dispose();
        _monitor = null;
        _hotkey?.Dispose();
        _hotkey = null;
        _catcher?.Close();
        _catcher = null;
        _dragWheel?.Close();
        _dragWheel = null;
        _clickWheel?.Close();
        _clickWheel = null;
    });

    public void ApplySettings() => OnUi("apply settings", ApplySettingsCore);

    public void ShowForFiles(IReadOnlyList<InputFile> files, WheelMode mode, ScreenPoint? at = null) =>
        OnUi("show for files", () => ShowClickWheel(files, mode, at, LaunchSource.Wheel));

    public void RenderPreviews(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            WheelPreviews.Render(_services.Catalog, outputDirectory, Log);
            return;
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                WheelPreviews.Render(_services.Catalog, outputDirectory, Log);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            Name = "Clementine wheel previews",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("Rendering the wheel previews failed.", failure);
    }

    // ---------------------------------------------------------------- settings

    void OnSettingsChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() => Guard("settings changed", () =>
        {
            if (_started)
                ApplySettingsCore();
        }));

    void ApplySettingsCore()
    {
        var settings = _services.Settings.Current;
        _bindings = ModifierBindings.From(settings);
        var enabled = _started && settings.WheelEnabled;

        if (enabled && !_bindings.IsEmpty)
        {
            _monitor ??= CreateMonitor();
            _monitor.Configure(_bindings);
            if (!_monitor.IsRunning && !_monitor.Start())
                Log.Warn("Wheel: the global drag monitor couldn't start, so Shift-drag won't open the wheel.");
        }
        else
        {
            // An open click-mode wheel still needs the hook to notice clicks outside it.
            if (_clickWheel?.IsVisible != true)
                _monitor?.Stop();
            HideCatcher();
            _dragWheel?.Dismiss();
        }

        if (enabled && HotkeyGesture.TryParse(settings.SelectionHotkey, out var gesture))
        {
            _hotkey ??= CreateHotkey();
            _hotkey.Register(gesture);
        }
        else
        {
            if (enabled && !string.IsNullOrWhiteSpace(settings.SelectionHotkey))
                Log.Warn($"Wheel: '{settings.SelectionHotkey}' isn't a hotkey Clementine can use (it needs Ctrl, Alt or Win).");
            _hotkey?.Unregister();
        }
    }

    DragMonitor CreateMonitor()
    {
        var monitor = new DragMonitor(_dispatcher, Log);
        monitor.Signal += signal => Guard("drag signal", () => OnDragSignal(signal));
        // The click-mode wheel may not get activation (and so no Deactivated) when opened from another process;
        // the hook still sees clicks elsewhere and closes it.
        monitor.PrimaryButtonDown += point => Guard("outside click", () =>
        {
            if (_clickWheel is { IsVisible: true } wheel && !wheel.Covers(point))
                wheel.Dismiss();
        });
        return monitor;
    }

    HotkeyManager CreateHotkey()
    {
        var hotkey = new HotkeyManager(Log);
        hotkey.Pressed += OnHotkeyPressed;
        return hotkey;
    }

    void WarmUp()
    {
        // Create the windows (hidden) now so the first wheel appears instantly.
        _ = Catcher.Handle;
        _ = DragWheel.Handle;
        // Fonts, glyph caches and the drawing code paths load once the app is idle, off the critical path.
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => Guard("warm up", () =>
        {
            WheelView.WarmUp();
            if (_started)
                WarmUpCatalog();
        }));
    }

    /// <summary>
    /// The first real drag would otherwise pay for the catalog query, every operation's availability check (tool
    /// lookups) and measuring the real labels — about as long again as a warm open. Do that once, while idle, for
    /// the kinds dragged most, on the drag wheel's own view (its text cache is per view).
    /// </summary>
    void WarmUpCatalog()
    {
        var folder = Path.Combine(AppPaths.TempRoot, $"wheel-warm-up-{Environment.ProcessId}");
        try
        {
            Directory.CreateDirectory(folder);
            var scale = PopupWindow.MonitorAt(NativeMethods.CursorPosition()).Scale;
            foreach (var name in new[] { "warm-up.mp4", "warm-up.png" })
            {
                var path = Path.Combine(folder, name);
                File.WriteAllBytes(path, []);
                var model = WheelModel.Build(_services.Catalog, [InputFile.FromPath(path)], WheelMode.Convert, Log);
                if (_dragWheel is { IsVisible: false } wheel)
                    wheel.View.Prime(WheelSession.ForDisplay(model, scale));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"Wheel: warm-up skipped ({ex.Message}).");
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ---------------------------------------------------------------- Shift-drag

    CatcherWindow Catcher
    {
        get
        {
            if (_catcher is null)
            {
                _catcher = new CatcherWindow(Log);
                _catcher.FilesEntered += (paths, at) => Guard("files entered", () => OnFilesEntered(paths, at));
                _catcher.ForeignDragEntered += () => Guard("foreign drag", () =>
                {
                    // Not files (text, a browser image...): stay out of the way for the rest of this drag only.
                    _suppressedDrag = _currentDrag;
                    HideCatcher();
                });
                _catcher.Dropped += e => Guard("catcher drop", () => _dragWheel?.AcceptForwardedDrop(e));
            }
            return _catcher;
        }
    }

    WheelWindow DragWheel
    {
        get
        {
            if (_dragWheel is null)
            {
                _dragWheel = new WheelWindow(WheelInteraction.Drag, Log) { MatchMode = held => _bindings.Match(held) };
                _dragWheel.ModeRequested += mode => Guard("mode switch", () => SwitchDragMode(mode));
                _dragWheel.Chosen += (option, at) => Launch(option, _dragFiles, LaunchSource.Wheel, at);
            }
            return _dragWheel;
        }
    }

    WheelWindow ClickWheel
    {
        get
        {
            if (_clickWheel is null)
            {
                _clickWheel = new WheelWindow(WheelInteraction.Click, Log);
                _clickWheel.Chosen += (option, at) => Launch(option, _clickFiles, _clickSource, at);
                _clickWheel.Dismissed += () => Guard("click wheel closed", () =>
                {
                    if (_monitor is null)
                        return;
                    _monitor.WatchClicks = false;
                    // The hook may have been started only to watch for outside clicks.
                    if (!MonitorsDrags)
                        _monitor.Stop();
                });
            }
            return _clickWheel;
        }
    }

    bool MonitorsDrags => _started && _services.Settings.Current.WheelEnabled && !_bindings.IsEmpty;

    /// <summary>Reacts to the drag monitor (internal for tests).</summary>
    internal void OnDragSignal(DragSignal signal)
    {
        switch (signal.Kind)
        {
            case DragSignalKind.Armed:
                _currentDrag = signal.Drag;
                _armedMode = signal.Mode ?? WheelMode.Convert;
                // MonitorsDrags: the hook also runs while paused (Stop) or disabled when it only watches for clicks
                // outside a click-mode wheel; Shift-drag must stay off then.
                if (signal.Drag == _suppressedDrag || _dragWheel?.IsVisible == true || !MonitorsDrags)
                    break;
                if (_checkedDrag != signal.Drag)
                {
                    _checkedDrag = signal.Drag;
                    if (CatcherBlocker(signal) is { } reason)
                    {
                        _suppressedDrag = signal.Drag;
                        if (reason != _lastHiddenReason)
                            Log.Info($"Wheel: not offering the wheel for this drag: {reason}.");
                        _lastHiddenReason = reason;
                        break;
                    }
                    _lastHiddenReason = null;
                }
                Catcher.ShowAt(signal.Point);
                _catcherRelease.Reset();
                _catcherWatch.Start();
                break;
            case DragSignalKind.Moved:
                if (_catcher?.IsVisible == true)
                    _catcher.MoveTo(signal.Point);
                break;
            case DragSignalKind.ModeChanged:
                _armedMode = signal.Mode ?? _armedMode;
                break;
            case DragSignalKind.Disarmed:
                HideCatcher();
                break;
            case DragSignalKind.Released:
                HideCatcher();
                if (_dragWheel?.IsVisible == true)
                {
                    // Let a pending Drop arrive first; it closes the wheel itself.
                    _releaseTimer.Stop();
                    _releaseTimer.Start();
                }
                break;
        }
    }

    void HideCatcher()
    {
        _catcherWatch.Stop();
        _catcher?.HideCatcher();
    }

    // Safety net: the catcher eats clicks where it stands (alpha 1/255 is not click-through), so it must never outlive
    // the drag, even if the button-up never reached the hook.
    void OnCatcherWatch()
    {
        if (_catcher?.IsVisible != true)
        {
            _catcherWatch.Stop();
            return;
        }
        if (_catcherRelease.Poll(IsButtonDown()))
        {
            Log.Info("Wheel: the drag ended without a button-up from the hook; hiding the catcher.");
            HideCatcher();
        }
    }

    /// <summary>Files entered the catcher at <paramref name="at"/>: open the drag wheel there (internal for tests).</summary>
    internal void OnFilesEntered(IReadOnlyList<string> paths, ScreenPoint at)
    {
        HideCatcher();
        if (_dragWheel?.IsVisible == true || !MonitorsDrags)
            return;
        var files = ToInputFiles(paths);
        if (files.Count == 0)
            return;
        _clickWheel?.Dismiss();
        _dragFiles = files;
        DragWheel.Open(WheelSession.ForDisplay(WheelModel.Build(_services.Catalog, files, _armedMode, Log), PopupWindow.MonitorAt(at).Scale), at);
    }

    void SwitchDragMode(WheelMode mode)
    {
        if (_dragWheel is not { IsVisible: true } wheel || _dragFiles.Count == 0)
            return;
        wheel.Replace(WheelSession.ForDisplay(WheelModel.Build(_services.Catalog, _dragFiles, mode, Log), wheel.PixelsPerDip));
    }

    void OnReleaseTimer()
    {
        _releaseTimer.Stop();
        if (_dragWheel?.IsVisible == true && !IsButtonDown())
            _dragWheel.Dismiss();
    }

    // ---------------------------------------------------------------- click mode & hotkey

    void ShowClickWheel(IReadOnlyList<InputFile> files, WheelMode mode, ScreenPoint? at, LaunchSource source)
    {
        if (files.Count == 0)
        {
            Log.Info("Wheel: nothing to show (no files).");
            return;
        }
        _dragWheel?.Dismiss();
        _clickFiles = files;
        _clickSource = source;
        WatchClicks();
        var centre = at ?? NativeMethods.CursorPosition();
        ClickWheel.Open(WheelSession.ForDisplay(WheelModel.Build(_services.Catalog, files, mode, Log), PopupWindow.MonitorAt(centre).Scale), centre);
    }

    // The click-mode wheel may not get activation (so no Deactivated) when another app holds the foreground; the hook
    // then is the only way to notice a click elsewhere. Start it even when Shift-drag is off or paused.
    void WatchClicks()
    {
        try
        {
            _monitor ??= CreateMonitor();
            _monitor.Configure(_bindings);
            if (!_monitor.IsRunning && !_monitor.Start())
                Log.Warn("Wheel: couldn't watch for clicks outside the wheel (mouse hook refused).");
            _monitor.WatchClicks = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Wheel: watching for outside clicks failed", ex);
        }
    }

    async void OnHotkeyPressed()
    {
        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            var cursor = NativeMethods.CursorPosition();
            var paths = await ExplorerSelection.GetSelectedPathsAsync(foreground, Log, TimeSpan.FromSeconds(3));
            if (paths.Count == 0)
            {
                Log.Info("Selection hotkey: no files are selected in the foreground Explorer window.");
                return;
            }
            ShowClickWheel(ToInputFiles(paths), WheelMode.Convert, cursor, LaunchSource.Hotkey);
        }
        catch (Exception ex)
        {
            Log.Warn("Selection hotkey failed", ex);
        }
    }

    // ---------------------------------------------------------------- shared

    void Launch(WheelOption option, IReadOnlyList<InputFile> files, LaunchSource source, ScreenPoint anchor)
    {
        // Run after the OLE Drop call has returned, so Explorer is never blocked by an options dialog.
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(async () =>
        {
            try
            {
                var launcher = _services.Launcher;
                if (launcher is null)
                {
                    Log.Warn($"Wheel: no launcher is set, so {option.Info.Id} can't run.");
                    return;
                }
                Log.Info($"Wheel: {option.Info.Id} on {files.Count} file(s) ({source}).");
                await launcher.LaunchAsync(option.Operation, files, source, anchor);
            }
            catch (Exception ex)
            {
                Log.Error($"Wheel: launching {option.Info.Id} failed", ex);
            }
        }));
    }

    IReadOnlyList<InputFile> ToInputFiles(IEnumerable<string> paths)
    {
        var files = new List<InputFile>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                files.Add(InputFile.FromPath(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Log.Warn($"Wheel: skipping '{path}': {ex.Message}");
            }
        }
        return files;
    }

    void OnUi(string what, Action action)
    {
        if (_dispatcher.CheckAccess())
            Guard(what, action);
        else
            _dispatcher.BeginInvoke(() => Guard(what, action));
    }

    void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"Wheel: {what} failed", ex);
        }
    }
}
