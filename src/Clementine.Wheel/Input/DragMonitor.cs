using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.Wheel;

/// <summary>
/// Global drag detection. A <c>WH_MOUSE_LL</c> hook runs on a dedicated background thread with its own message
/// loop (a busy UI thread would make Windows silently drop the hook); it drives a <see cref="DragDetector"/> there
/// and marshals the interesting signals to the UI dispatcher. Mouse moves are coalesced so the UI never queues up.
/// </summary>
internal sealed class DragMonitor : IDisposable
{
    const uint TickMilliseconds = 35;
    const uint DefaultHealthMilliseconds = 3000;

    readonly Dispatcher _dispatcher;
    readonly ILog _log;
    readonly NativeMethods.LowLevelMouseProc _proc;
    readonly DragDetector _detector = new();
    readonly object _gate = new();
    readonly uint _healthMilliseconds;
    int _healthChecks;

    Thread? _thread;
    volatile uint _threadId;
    IntPtr _hook;
    // Thread timers (no window): Windows assigns the ids, so keep what SetTimer returns.
    UIntPtr _tickTimer;
    UIntPtr _healthTimer;
    volatile ModifierBindings _bindings = ModifierBindings.Default;
    volatile int _thresholdX = 8;
    volatile int _thresholdY = 8;
    long _lastEventTicks;
    NativeMethods.POINT _lastHookPoint;
    ScreenPoint _latestPoint;
    int _movePending;
    volatile bool _watchClicks;

    public DragMonitor(Dispatcher dispatcher, ILog log)
        : this(dispatcher, log, TimeSpan.FromMilliseconds(DefaultHealthMilliseconds))
    {
    }

    /// <param name="healthInterval">How often the hook thread checks that Windows hasn't dropped the hook.</param>
    internal DragMonitor(Dispatcher dispatcher, ILog log, TimeSpan healthInterval)
    {
        _dispatcher = dispatcher;
        _log = log;
        _proc = HookProc;
        _healthMilliseconds = (uint)Math.Clamp(healthInterval.TotalMilliseconds, 10, 60_000);
    }

    /// <summary>How many health checks the hook thread has run (tests).</summary>
    internal int HealthChecks => Volatile.Read(ref _healthChecks);

    /// <summary>Raised on the UI thread for every non-trivial detector signal.</summary>
    public event Action<DragSignal>? Signal;

    /// <summary>Raised on the UI thread for primary-button presses while <see cref="WatchClicks"/> is on.</summary>
    public event Action<ScreenPoint>? PrimaryButtonDown;

    /// <summary>Report every primary-button press (used to close the click-mode wheel on an outside click).</summary>
    public bool WatchClicks
    {
        get => _watchClicks;
        set => _watchClicks = value;
    }

    public bool IsRunning => _thread is { IsAlive: true } && _hook != IntPtr.Zero;

    /// <summary>The most recent cursor position seen by the hook (physical pixels).</summary>
    public ScreenPoint LatestPoint
    {
        get
        {
            lock (_gate)
                return _latestPoint;
        }
    }

    /// <summary>Updates the modifier bindings and drag threshold (takes effect on the next event).</summary>
    public void Configure(ModifierBindings bindings)
    {
        _bindings = bindings;
        _thresholdX = Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDRAG)) * 2;
        _thresholdY = Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDRAG)) * 2;
    }

    /// <summary>Starts the hook thread and waits until the hook is installed. Returns false if Windows refused it.</summary>
    public bool Start(TimeSpan? timeout = null)
    {
        if (_thread is { IsAlive: true })
            return _hook != IntPtr.Zero;

        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = "Clementine drag monitor",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread = thread;
        thread.Start();
        if (!ready.Wait(timeout ?? TimeSpan.FromSeconds(3)))
            _log.Warn("Drag monitor: the hook thread did not start in time.");
        return _hook != IntPtr.Zero;
    }

    public void Stop()
    {
        var thread = _thread;
        if (thread is null)
            return;
        if (_threadId != 0)
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (!thread.Join(TimeSpan.FromSeconds(3)))
            _log.Warn("Drag monitor: the hook thread did not stop in time.");
        _thread = null;
    }

    public void Dispose() => Stop();

    void Run(ManualResetEventSlim ready)
    {
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            Install();
            _healthTimer = NativeMethods.SetTimer(IntPtr.Zero, UIntPtr.Zero, _healthMilliseconds, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            _log.Error("Drag monitor: couldn't install the mouse hook", ex);
        }
        finally
        {
            ready.Set();
        }

        try
        {
            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == NativeMethods.WM_TIMER && msg.hwnd == IntPtr.Zero)
                {
                    OnTimer(msg.wParam);
                    continue;
                }
                NativeMethods.TranslateMessage(in msg);
                NativeMethods.DispatchMessage(in msg);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Drag monitor: message loop failed", ex);
        }
        finally
        {
            StopTicking();
            if (_healthTimer != UIntPtr.Zero)
                NativeMethods.KillTimer(IntPtr.Zero, _healthTimer);
            _healthTimer = UIntPtr.Zero;
            Uninstall();
            _threadId = 0;
        }
    }

    void Install()
    {
        Uninstall();
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            _log.Warn($"Drag monitor: SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()}).");
        Interlocked.Exchange(ref _lastEventTicks, Stopwatch.GetTimestamp());
        // Seed the health check with the real cursor position, or the first check sees "moved from (0,0)".
        NativeMethods.GetCursorPos(out _lastHookPoint);
    }

    void Uninstall()
    {
        if (_hook == IntPtr.Zero)
            return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        // Low-level hooks only ever get HC_ACTION; anything else must be passed on untouched. Injected input
        // (LLMHF_INJECTED: remote desktop tools, tablets, accessibility software) is handled like real input on
        // purpose, so Shift-drag works there too.
        if (code == NativeMethods.HC_ACTION)
        {
            try
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                Process((int)wParam, data.pt);
            }
            catch (Exception ex)
            {
                // Never let an exception escape into the hook chain.
                _log.Warn("Drag monitor: hook callback failed", ex);
            }
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    void Process(int message, NativeMethods.POINT pt)
    {
        _lastHookPoint = pt;
        Interlocked.Exchange(ref _lastEventTicks, Stopwatch.GetTimestamp());
        var point = new ScreenPoint(pt.X, pt.Y);
        SyncSettings();

        switch (message)
        {
            case NativeMethods.WM_LBUTTONDOWN:
                _detector.ButtonDown(point);
                StartTicking();
                if (_watchClicks)
                    _dispatcher.BeginInvoke(DispatcherPriority.Input, () => RaiseButtonDown(point));
                break;
            case NativeMethods.WM_MOUSEMOVE when _detector.Phase != DragPhase.Idle:
                Publish(_detector.Move(point, ModifierBindings.ReadHeld()));
                break;
            case NativeMethods.WM_LBUTTONUP:
                StopTicking();
                Publish(_detector.ButtonUp(point));
                break;
        }
    }

    void OnTimer(IntPtr timerId)
    {
        try
        {
            var id = (UIntPtr)(nuint)timerId;
            if (id == _healthTimer)
            {
                CheckHealth();
                return;
            }
            if (id != _tickTimer)
                return;
            if (_detector.Phase == DragPhase.Idle)
            {
                StopTicking();
                return;
            }
            var cursor = NativeMethods.CursorPosition();
            if (!NativeMethods.IsPrimaryButtonDown())
            {
                // We missed the button-up (e.g. it happened on a secure desktop): end the drag cleanly.
                StopTicking();
                Publish(_detector.ButtonUp(cursor));
                return;
            }
            SyncSettings();
            Publish(_detector.Tick(cursor, ModifierBindings.ReadHeld()));
        }
        catch (Exception ex)
        {
            _log.Warn("Drag monitor: timer tick failed", ex);
        }
    }

    // Windows silently removes a low-level hook whose callback once took too long. If the cursor moved but we
    // heard nothing for a while, reinstall it.
    void CheckHealth()
    {
        Interlocked.Increment(ref _healthChecks);
        var idle = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastEventTicks));
        if (idle < TimeSpan.FromMilliseconds(_healthMilliseconds))
            return;
        if (!NativeMethods.GetCursorPos(out var now) || (now.X == _lastHookPoint.X && now.Y == _lastHookPoint.Y))
            return;
        _log.Info("Drag monitor: the mouse hook went quiet while the cursor moved; reinstalling it.");
        // Events were lost. A drag still in progress is finished by the tick timer when the button goes up; if the
        // button is already up, end it now so the catcher can't stay on screen (it was silently reset before).
        if (!NativeMethods.IsPrimaryButtonDown())
        {
            StopTicking();
            Publish(_detector.Abort(new ScreenPoint(now.X, now.Y)));
        }
        Install();
        _lastHookPoint = now;
    }

    void SyncSettings()
    {
        _detector.Bindings = _bindings;
        _detector.ThresholdX = _thresholdX;
        _detector.ThresholdY = _thresholdY;
    }

    void StartTicking()
    {
        if (_tickTimer == UIntPtr.Zero)
            _tickTimer = NativeMethods.SetTimer(IntPtr.Zero, UIntPtr.Zero, TickMilliseconds, IntPtr.Zero);
    }

    void StopTicking()
    {
        if (_tickTimer == UIntPtr.Zero)
            return;
        NativeMethods.KillTimer(IntPtr.Zero, _tickTimer);
        _tickTimer = UIntPtr.Zero;
    }

    void RaiseButtonDown(ScreenPoint point)
    {
        try
        {
            PrimaryButtonDown?.Invoke(point);
        }
        catch (Exception ex)
        {
            _log.Warn("Drag monitor: handling a click failed", ex);
        }
    }

    void Publish(DragSignal signal)
    {
        if (signal.Kind == DragSignalKind.None)
            return;

        lock (_gate)
            _latestPoint = signal.Point;

        if (signal.Kind == DragSignalKind.Moved)
        {
            // Coalesce: one pending move at a time; the UI reads LatestPoint.
            if (Interlocked.Exchange(ref _movePending, 1) == 1)
                return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (signal.Kind == DragSignalKind.Moved)
                Interlocked.Exchange(ref _movePending, 0);
            try
            {
                Signal?.Invoke(signal.Kind == DragSignalKind.Moved ? signal with { Point = LatestPoint } : signal);
            }
            catch (Exception ex)
            {
                _log.Warn($"Drag monitor: handling {signal.Kind} failed", ex);
            }
        });
    }
}
