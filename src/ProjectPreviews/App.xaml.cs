using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ProjectPreviews.Core;
using ProjectPreviews.Platform;
using ProjectPreviews.Services;
using ProjectPreviews.UI;

namespace ProjectPreviews;

public partial class App : Application
{
    private const int HotkeyPicker = 1, HotkeyEscape = 2;
    private const int DwellMs = 120, LeaveGraceMs = 300, ButtonCacheMs = 4000;

    private Mutex? _mutex;
    private EventWaitHandle? _signal;
    private string _settingsPath = SettingsStore.DefaultPath;
    private HwndSource? _messages;
    private uint _taskbarCreatedMsg;
    private System.Windows.Forms.NotifyIcon? _tray;
    private ContextMenu? _menu;
    private MenuItem? _menuToggle, _menuPicker;
    private PickerWindow? _picker;
    private SettingsWindow? _settingsWindow;
    private MouseWatcher? _mouse;
    private readonly Dictionary<nint, BitmapSource> _images = [];
    private readonly DispatcherTimer _dwell = new() { Interval = TimeSpan.FromMilliseconds(DwellMs) };
    private readonly DispatcherTimer _leave = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private Native.POINT _anchor;
    private long _anchorAt;
    private (List<ProjectWindow> Windows, long At) _prepared = ([], 0);
    private (Native.RECT Rect, long At)? _buttonCache;
    private Native.RECT? _hoverButton, _suppress;
    private bool _queryRunning;
    private long _outsideSince;
    private int _captureGen;
    private bool _escapeRegistered;
    private readonly List<nint> _liveHooks = [];
    private Native.WinEventProc? _liveProc;
    private bool _exiting;

    public AppSettings Settings { get; private set; } = new();
    public WindowCatalog Catalog { get; private set; } = null!;
    public string? SettingsProblem { get; private set; }
    public string? ShortcutProblem { get; private set; }
    public IReadOnlyDictionary<nint, BitmapSource> Images => _images;
    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        _mutex = new Mutex(true, @"Local\ProjectPreviews.Instance", out bool first);
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ProjectPreviews.ShowSettings");
        if (!first)
        {
            _signal.Set(); // the running copy opens its settings
            _exiting = true;
            Shutdown();
            return;
        }
        ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, -1, false);

        if (e.Args.SkipWhile(a => a != "--settings-path").Skip(1).FirstOrDefault() is { } custom) _settingsPath = custom; // for testing
        bool firstRun = !File.Exists(_settingsPath);
        Settings = SettingsStore.Load(_settingsPath, out var problem);
        SettingsProblem = problem;
        ThemeManager.Apply(Settings.Theme);

        Catalog = new WindowCatalog(new ClaudeWorkersAliases(ClaudeWorkersAliases.DefaultPath), () => Settings);
        _picker = new PickerWindow();
        _picker.CardChosen += OnCardChosen;
        _picker.TabClicked += OnTabClicked;
        _picker.DismissRequested += () => HidePicker();
        _mouse = new MouseWatcher(Dispatcher);
        _mouse.TaskbarMove += OnTaskbarMove;
        _mouse.TaskbarLeave += () => { _dwell.Stop(); _anchor = new Native.POINT { X = int.MinValue / 2 }; if (_picker?.IsOpen != true) _suppress = null; };
        _mouse.MouseDown += (pt, _) => { if (_picker!.IsOpen && !InHot(pt)) HidePicker(); };
        _dwell.Tick += (_, _) => OnDwell();
        _leave.Tick += (_, _) => OnLeaveTick();
        _refresh.Tick += (_, _) => { _refresh.Stop(); RefreshOpenPicker(); };

        _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        _messages = new HwndSource(new HwndSourceParameters("Project Previews messages") { Width = 0, Height = 0, WindowStyle = 0 });
        _messages.AddHook(WndProc);

        CreateTray();
        ApplyEnabled();
        SystemEvents.DisplaySettingsChanged += OnEnvironmentChanged;
        SystemEvents.PowerModeChanged += OnPowerChanged;
        ThemeManager.Changed += UpdateTrayMenu;

        if (problem is not null) Notify(problem);
        if (firstRun && !background) ShowSettings();
        else if (!background) Notify(Settings.Enabled ? $"Running. Hover the VS Code taskbar icon or press {Settings.Shortcut}." : "Running, but previews are disabled.");
    }

    // ---------- enable / disable ----------

    public void ApplyEnabled()
    {
        if (_exiting) return;
        Unregister(HotkeyPicker);
        ShortcutProblem = null;
        if (!Settings.Enabled)
        {
            HidePicker();
            _mouse!.Stop();
            Catalog.StopTracking();
            _images.Clear(); // no screenshots kept while disabled
        }
        else
        {
            Catalog.StartTracking();
            if (Settings.HoverEnabled)
            {
                _mouse!.Start();
                // The first UI Automation call into Explorer is slow (~200 ms); pay it now rather than on the first hover.
                var bars = MouseWatcher.FindTaskbars();
                if (bars.Length > 0) Task.Run(() => TaskbarButtons.VsCodeButtonAt(new Native.POINT { X = bars[0].Left + 1, Y = bars[0].Top + 1 }));
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    if (Settings.Enabled && _picker?.IsVisible == false) _picker.WarmUp(Catalog.Snapshot(), Settings, _images);
                });
            }
            else _mouse!.Stop();
            if (Settings.ShortcutEnabled)
            {
                var sc = Shortcut.Parse(Settings.Shortcut);
                if (sc is null) ShortcutProblem = $"\"{Settings.Shortcut}\" isn't a valid shortcut.";
                else if (!Native.RegisterHotKey(_messages!.Handle, HotkeyPicker, sc.Value.Modifiers | Native.MOD_NOREPEAT, sc.Value.VirtualKey))
                    ShortcutProblem = $"{sc.Value.Text} is already used by another app. Choose a different shortcut.";
            }
        }
        UpdateTrayMenu();
    }

    public void SaveSettings()
    {
        try { SettingsStore.Save(_settingsPath, Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify($"Couldn't save settings: {ex.Message}"); }
    }

    private void Unregister(int id)
    {
        if (_messages is not null) Native.UnregisterHotKey(_messages.Handle, id);
        if (id == HotkeyEscape) _escapeRegistered = false;
    }

    // ---------- taskbar hover ----------

    private void OnTaskbarMove(Native.POINT pt)
    {
        Log.Write($"taskbar move {pt.X},{pt.Y}");
        if (!Settings.Enabled || !Settings.HoverEnabled || _picker!.IsOpen) return;
        if (_suppress is { } s) { if (s.Contains(pt)) return; _suppress = null; }
        if (Environment.TickCount64 - _prepared.At > 2000)
        {
            // Pointer is on the taskbar: get the cards ready now so a hover only has to position and show them.
            var windows = Catalog.Snapshot();
            _prepared = (windows, Environment.TickCount64);
            _picker.Prewarm(windows, Settings, _images);
        }
        if (Math.Abs(pt.X - _anchor.X) > 4 || Math.Abs(pt.Y - _anchor.Y) > 4)
        {
            _anchor = pt;
            _anchorAt = Environment.TickCount64;
            _dwell.Stop();
            _dwell.Start();
        }
        // Locate the button while the pointer is still settling, so the dwell can show without waiting on UI Automation.
        if (!(_buttonCache is { } c && Environment.TickCount64 - c.At < ButtonCacheMs) || !_buttonCache.Value.Rect.Contains(pt))
            QueryButton(pt, showIfStill: false);
    }

    private void QueryButton(Native.POINT at, bool showIfStill)
    {
        if (_queryRunning) return;
        _queryRunning = true;
        Task.Run(() => TaskbarButtons.VsCodeButtonAt(at)).ContinueWith(t =>
        {
            _queryRunning = false;
            Log.Write($"uia result {t.Result?.ToString() ?? "none"}");
            if (t.Result is not { } rect) return;
            _buttonCache = (rect, Environment.TickCount64);
            Native.GetCursorPos(out var now);
            // Show if the dwell already elapsed while we were asking.
            if ((showIfStill || !_dwell.IsEnabled) && rect.Contains(now) && _suppress is null && IsStill(now)) ShowHover(rect);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private bool IsStill(Native.POINT p) => Math.Abs(p.X - _anchor.X) <= 4 && Math.Abs(p.Y - _anchor.Y) <= 4;

    /// <summary>The pointer rested on the taskbar: ask UI Automation what's under it (off the UI thread), then show if it's VS Code.</summary>
    private void OnDwell()
    {
        _dwell.Stop();
        Native.GetCursorPos(out var cur);
        Log.Write($"dwell at {cur.X},{cur.Y}");
        if (_buttonCache is { } c && Environment.TickCount64 - c.At < ButtonCacheMs && c.Rect.Contains(cur))
        {
            ShowHover(c.Rect);
            // Taskbar buttons shift when apps open or close; confirm the cached spot is still VS Code's button.
            Task.Run(() => TaskbarButtons.VsCodeButtonAt(cur)).ContinueWith(t =>
            {
                if (t.Result is { } now && now.Left == c.Rect.Left && now.Right == c.Rect.Right) return;
                _buttonCache = null;
                if (_picker!.IsOpen && _picker.Mode == PickerMode.Hover) { Log.Write("cached button was stale; closing"); HidePicker(); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        QueryButton(cur, showIfStill: true); // if a query is already running, its completion shows instead
    }

    private void ShowHover(Native.RECT button)
    {
        if (!Settings.Enabled || !Settings.HoverEnabled || _picker!.IsOpen) return;
        long t0 = Environment.TickCount64;
        var windows = t0 - _prepared.At < 1500 ? _prepared.Windows : Catalog.Snapshot();
        _prepared = ([], 0);
        if (windows.Count == 0) return;
        long t1 = Environment.TickCount64;
        _hoverButton = button;
        _picker.Present(windows, Settings, PickerMode.Hover, button, _images, keepOrder: false);
        Log.Write($"shown: snapshot {t1 - t0}ms, present {Environment.TickCount64 - t1}ms");
        ScheduleNudges();
        // Esc must work without focusing the popup; registered only while it's open.
        _escapeRegistered = Native.RegisterHotKey(_messages!.Handle, HotkeyEscape, 0, 0x1B);
        _outsideSince = 0;
        _leave.Start();
        AfterShow(windows);
    }

    /// <summary>
    /// Explorer only notices the pointer has left its button when the mouse next moves, and (measured on Windows 11)
    /// ignores a leave in roughly the first 250 ms of a hover. So while our popup covers the button we deliver an
    /// invisible 1-pixel move ~300 and ~500 ms after the pointer came to rest, before Explorer's thumbnail opens
    /// (~780 ms). If the hand moves on its own, those real moves do the same job.
    /// </summary>
    private void ScheduleNudges()
    {
        // Runs off the UI thread: the UI may be busy painting previews right when the nudge is due.
        nint popup = _picker!.Handle;
        var button = _hoverButton!.Value;
        long rest = _anchorAt;
        // Own thread, not the thread pool: right after startup the pool can be slow to schedule, and timing matters here.
        new Thread(() =>
        {
            // Try every 40 ms from ~300 ms after rest; after the first nudge that lands, send one more 200 ms later.
            long next = Math.Max(rest + 300, Environment.TickCount64 + 40), deadline = rest + 750;
            int landed = 0;
            while (landed < 2 && Environment.TickCount64 < deadline + (landed > 0 ? 300 : 0))
            {
                long wait = next - Environment.TickCount64;
                if (wait > 0) Thread.Sleep((int)wait);
                if (!Native.IsWindowVisible(popup) || !Native.GetCursorPos(out var p) || !button.Contains(p)) return;
                // Explorer raises its (also topmost) taskbar during a hover; put the popup back above it.
                Native.SetWindowPos(popup, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                // Only nudge once our popup (painted, so no longer click-through) is what's under the pointer.
                if (Native.GetAncestor(Native.WindowFromPoint(p), 2 /* GA_ROOT */) == popup)
                {
                    Native.NudgePointer();
                    landed++;
                    Log.Write($"nudged {Environment.TickCount64 - rest}ms after rest");
                    next = Environment.TickCount64 + 200;
                }
                else next = Environment.TickCount64 + 40;
            }
        }) { IsBackground = true, Name = "Project Previews nudge" }.Start();
    }

    public void OpenPicker()
    {
        if (!Settings.Enabled) { Notify("Previews are disabled. Enable them from the tray menu."); return; }
        if (_picker!.IsOpen)
        {
            if (_picker.Mode == PickerMode.Keyboard) HidePicker();
            else { _leave.Stop(); Unregister(HotkeyEscape); _picker.SwitchToKeyboard(); }
            return;
        }
        var windows = Catalog.Snapshot();
        if (windows.Count == 0) { Notify("No VS Code windows are open."); return; }
        _hoverButton = null;
        _picker.Present(windows, Settings, PickerMode.Keyboard, null, _images, keepOrder: false);
        AfterShow(windows);
    }

    private void AfterShow(List<ProjectWindow> windows)
    {
        _mouse!.ReportClicks = true;
        if (!_mouse.Running) _mouse.Start(); // outside-click detection also in shortcut-only mode
        foreach (var dead in _images.Keys.Where(h => !Native.IsWindow(h)).ToList()) _images.Remove(dead);
        StartLiveHooks(windows);
        long shownAt = Environment.TickCount64;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Log.Write($"first frame after {Environment.TickCount64 - shownAt}ms"));
        // In hover mode wait until Explorer's hover window has passed: repainting for fresh previews right away would
        // delay our first frame, and an unpainted layered window is click-through. Cached previews show meanwhile.
        int delay = _picker!.Mode == PickerMode.Hover ? 700 : 0;
        int gen = ++_captureGen;
        Task.Delay(delay).ContinueWith(_ =>
        {
            if (gen == _captureGen && _picker.IsOpen) StartCaptures(windows.Where(w => !w.Minimized).Select(w => w.Hwnd));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void HidePicker()
    {
        _leave.Stop();
        _refresh.Stop();
        if (_escapeRegistered) Unregister(HotkeyEscape);
        StopLiveHooks();
        _captureGen++;
        if (_mouse is not null)
        {
            _mouse.ReportClicks = false;
            if (!Settings.Enabled || !Settings.HoverEnabled) _mouse.Stop();
        }
        if (_picker?.IsOpen != true) return;
        _picker.Hide();
        // Don't pop straight back up while the pointer is still resting on the button.
        if (_hoverButton is { } b && Native.GetCursorPos(out var p) && b.Contains(p)) _suppress = b;
    }

    private bool InHot(Native.POINT p) => _picker!.HotRects.Any(r => r.Contains(p));

    private void OnLeaveTick()
    {
        if (_picker!.Mode != PickerMode.Hover || !_picker.IsOpen) { _leave.Stop(); return; }
        Native.GetCursorPos(out var p);
        if (InHot(p)) { _outsideSince = 0; return; }
        long now = Environment.TickCount64;
        if (_outsideSince == 0) _outsideSince = now;
        else if (now - _outsideSince >= LeaveGraceMs) HidePicker();
    }

    /// <summary>Clicks on the part of the popup that covers the VS Code taskbar button.</summary>
    private void OnTabClicked(int button)
    {
        var rect = _hoverButton;
        if (button == 0)
        {
            var order = _picker!.Order.Where(Native.IsWindow).ToList();
            if (order.Count == 1)
            {
                // Same as the taskbar: one window toggles between minimized and in front.
                nint h = order[0];
                HidePicker();
                if (Native.GetForegroundWindow() == h && !Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_MINIMIZE);
                else WindowSwitcher.Activate(h);
            }
            else
            {
                // Several windows: the taskbar would open its own list; ours is already open, so hand it the keyboard.
                _leave.Stop();
                Unregister(HotkeyEscape);
                _picker.SwitchToKeyboard();
            }
            return;
        }
        // Right/middle click: get out of the way and pass the click to the real button (jump list / new window).
        HidePicker();
        _suppress = rect;
        uint down = button == 1 ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_MIDDLEDOWN;
        uint up = button == 1 ? Native.MOUSEEVENTF_RIGHTUP : Native.MOUSEEVENTF_MIDDLEUP;
        Native.SendInput(2, [new Native.INPUT { type = 0, dwFlags = down }, new Native.INPUT { type = 0, dwFlags = up }],
            System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    private void OnCardChosen(nint hwnd)
    {
        if (!Native.IsWindow(hwnd)) { RefreshOpenPicker(); return; }
        if (WindowSwitcher.Activate(hwnd)) { HidePicker(); return; }
        // Windows' foreground rules said no: say so instead of pretending.
        Native.FlashWindow(hwnd, true);
        _picker!.ShowProblem("Windows didn't allow the switch. The VS Code taskbar button is flashing.");
    }

    // ---------- while open: follow window changes ----------

    private void StartLiveHooks(List<ProjectWindow> windows)
    {
        StopLiveHooks();
        _liveProc ??= (hook, ev, hwnd, idObject, idChild, thread, time) =>
        {
            if (idObject != 0 || hwnd == 0) return; // OBJID_WINDOW only
            if (_picker!.Order.Contains(hwnd) || (ev == 0x8002 && Catalog.IsCodeWindow(hwnd)))
            {
                _refresh.Stop();
                _refresh.Start();
            }
        };
        var pids = windows.Select(w => { Native.GetWindowThreadProcessId(w.Hwnd, out int pid); return (uint)pid; }).Distinct();
        foreach (var pid in pids)
        {
            // Destroy/show/hide/name change, and minimize start/end, only for VS Code's own process.
            _liveHooks.Add(Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_NAMECHANGE, 0, _liveProc, pid, 0, Native.WINEVENT_OUTOFCONTEXT));
            _liveHooks.Add(Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND, 0, _liveProc, pid, 0, Native.WINEVENT_OUTOFCONTEXT));
        }
    }

    private void StopLiveHooks()
    {
        foreach (var h in _liveHooks) if (h != 0) Native.UnhookWinEvent(h);
        _liveHooks.Clear();
    }

    private void RefreshOpenPicker()
    {
        if (_picker?.IsOpen != true) return;
        var windows = Catalog.Snapshot();
        if (windows.Count == 0) { HidePicker(); return; }
        var known = _picker.Order.ToHashSet();
        _picker.Present(windows, Settings, _picker.Mode, _picker.Mode == PickerMode.Hover ? _hoverButton : null, _images, keepOrder: true);
        StartLiveHooks(windows);
        StartCaptures(windows.Where(w => !w.Minimized && !known.Contains(w.Hwnd)).Select(w => w.Hwnd));
    }

    private void StartCaptures(IEnumerable<nint> hwnds)
    {
        int gen = ++_captureGen;
        var list = hwnds.ToList();
        double blur = Settings.Blur;
        Task.Run(() =>
        {
            foreach (var h in list)
            {
                if (gen != _captureGen) return;
                var img = Capture.Snapshot(h);
                // Blur is in card DIPs; the image is shown CardView.W DIPs wide.
                if (img is not null) img = Capture.Blur(img, blur * img.PixelWidth / CardView.W);
                Dispatcher.BeginInvoke(() =>
                {
                    if (!Settings.Enabled) return;
                    if (img is null) { _images.Remove(h); return; }
                    _images[h] = img;
                    if (gen == _captureGen && _picker!.IsOpen) _picker.SetImage(h, img);
                });
            }
        });
    }

    // ---------- environment ----------

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            if ((int)wParam == HotkeyPicker) OpenPicker();
            else if ((int)wParam == HotkeyEscape) HidePicker();
            handled = true;
        }
        else if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            // Explorer restarted: the taskbar (and the button we covered) is new.
            OnEnvironmentChanged(null, EventArgs.Empty);
        }
        else if (msg == 0x001A) // WM_SETTINGCHANGE: taskbar moved, auto-hide toggled...
        {
            _mouse?.InvalidateTaskbars();
            _buttonCache = null;
        }
        return 0;
    }

    private void OnEnvironmentChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        HidePicker();
        _buttonCache = null;
        _suppress = null;
        _mouse?.InvalidateTaskbars();
    });

    private void OnPowerChanged(object? sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (e.Mode == PowerModes.Suspend) HidePicker();
        else if (e.Mode == PowerModes.Resume)
        {
            OnEnvironmentChanged(null, EventArgs.Empty);
            // Re-install the mouse hook in case Windows dropped it during sleep.
            if (_mouse!.Running) { _mouse.Stop(); _mouse.Start(); }
        }
    });

    // ---------- tray ----------

    private void CreateTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, System.Windows.Forms.SystemInformation.SmallIconSize),
            Text = "Project Previews",
            Visible = true,
        };
        _menuToggle = new MenuItem();
        _menuToggle.Click += (_, _) => { Settings.Enabled = !Settings.Enabled; SaveSettings(); ApplyEnabled(); _settingsWindow?.Reload(); };
        _menuPicker = new MenuItem { Header = "Open window picker" };
        _menuPicker.Click += (_, _) => OpenPicker();
        var settings = new MenuItem { Header = "Settings…" };
        settings.Click += (_, _) => ShowSettings();
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitApp();
        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        _menu.Items.Add(_menuToggle);
        _menu.Items.Add(_menuPicker);
        _menu.Items.Add(settings);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exit);
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Right)
            {
                _menu.IsOpen = true;
                // The menu needs the foreground or it won't close when you click elsewhere.
                if (PresentationSource.FromVisual(_menu) is HwndSource src) Native.SetForegroundWindow(src.Handle);
            }
            else if (e.Button == System.Windows.Forms.MouseButtons.Left) OpenPicker();
        };
        UpdateTrayMenu();
    }

    private void UpdateTrayMenu()
    {
        if (_tray is null || _menuToggle is null) return;
        _menuToggle.Header = Settings.Enabled ? "Disable previews" : "Enable previews";
        _menuPicker!.IsEnabled = Settings.Enabled;
        _menuPicker.InputGestureText = Settings.Enabled && Settings.ShortcutEnabled && ShortcutProblem is null ? Settings.Shortcut : "";
        _tray.Text = Settings.Enabled ? "Project Previews" : "Project Previews (disabled)";
    }

    public void Notify(string text)
    {
        if (_tray is null) return;
        _tray.BalloonTipTitle = "Project Previews";
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(4000);
    }

    public void ShowSettings()
    {
        if (_exiting) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Cleanup();
        Shutdown();
    }

    private void Cleanup()
    {
        HidePicker();
        _mouse?.Stop();
        Catalog?.Dispose();
        Unregister(HotkeyPicker);
        Unregister(HotkeyEscape);
        SystemEvents.DisplaySettingsChanged -= OnEnvironmentChanged;
        SystemEvents.PowerModeChanged -= OnPowerChanged;
        _images.Clear();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _settingsWindow?.Close();
        _picker?.Close();
        _messages?.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex is not null && !_exiting) Cleanup();
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        ExitApp();
        base.OnSessionEnding(e);
    }
}
