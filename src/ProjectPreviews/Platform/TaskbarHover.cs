using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Threading;

namespace ProjectPreviews.Platform;

/// <summary>
/// Watches the mouse with a low-level hook (WH_MOUSE_LL: runs in this process, no DLL injection) on a
/// dedicated thread that only compares coordinates, so the system's mouse input is never held up.
/// Anything interesting is handed to the UI thread: moves over a taskbar, and (while
/// <see cref="ReportClicks"/> is set) mouse-button presses anywhere.
/// </summary>
public sealed class MouseWatcher : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly Native.HookProc _proc;
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private Native.RECT[] _taskbars = [];
    private long _taskbarsAt;
    private bool _inTaskbar;
    private int _movePending;
    private Native.POINT _lastMove;

    public MouseWatcher(Dispatcher ui)
    {
        _ui = ui;
        _proc = HookCallback;
    }

    public event Action<Native.POINT>? TaskbarMove;
    public event Action? TaskbarLeave;
    public event Action<Native.POINT, int>? MouseDown; // 0 left, 1 right, 2 middle

    public volatile bool ReportClicks;
    public bool Running => _thread is not null;

    public void Start()
    {
        if (_thread is not null) return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
            ready.Set();
            while (Native.GetMessage(out var msg, 0, 0, 0) > 0) { }
            if (_hook != 0) Native.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }) { IsBackground = true, Name = "Project Previews mouse hook" };
        _thread.Start();
        ready.Wait();
    }

    public void Stop()
    {
        if (_thread is null) return;
        Native.PostThreadMessage(_threadId, Native.WM_QUIT, 0, 0);
        _thread.Join(2000);
        _thread = null;
        _inTaskbar = false;
    }

    public void Dispose() => Stop();

    /// <summary>Forget cached taskbar positions (Explorer restarted, displays changed, taskbar moved).</summary>
    public void InvalidateTaskbars() => Interlocked.Exchange(ref _taskbarsAt, 0);

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if (msg == Native.WM_MOUSEMOVE)
            {
                bool inside = InTaskbar(info.pt);
                if (inside)
                {
                    _lastMove = info.pt;
                    // Coalesce: at most one pending hand-off to the UI thread.
                    if (Interlocked.Exchange(ref _movePending, 1) == 0)
                        _ui.BeginInvoke(() => { Interlocked.Exchange(ref _movePending, 0); TaskbarMove?.Invoke(_lastMove); });
                }
                else if (_inTaskbar)
                {
                    _ui.BeginInvoke(() => TaskbarLeave?.Invoke());
                }
                _inTaskbar = inside;
            }
            else if (ReportClicks && msg is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN)
            {
                int b = msg == Native.WM_LBUTTONDOWN ? 0 : msg == Native.WM_RBUTTONDOWN ? 1 : 2;
                var pt = info.pt;
                _ui.BeginInvoke(() => MouseDown?.Invoke(pt, b));
            }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool InTaskbar(Native.POINT p)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _taskbarsAt) > 2000)
        {
            _taskbars = FindTaskbars();
            Interlocked.Exchange(ref _taskbarsAt, now);
        }
        foreach (var r in _taskbars) if (r.Contains(p)) return true;
        return false;
    }

    /// <summary>The primary taskbar and any secondary-monitor taskbars, in physical pixels.</summary>
    public static Native.RECT[] FindTaskbars()
    {
        var list = new List<Native.RECT>();
        nint main = Native.FindWindow("Shell_TrayWnd", null);
        if (main != 0 && Native.GetWindowRect(main, out var r)) list.Add(r);
        nint h = 0;
        while ((h = Native.FindWindowEx(0, h, "Shell_SecondaryTrayWnd", null)) != 0)
            if (Native.GetWindowRect(h, out var s)) list.Add(s);
        return list.ToArray();
    }
}

/// <summary>Asks UI Automation (Explorer's own accessibility tree) what taskbar button is under a point.</summary>
public static class TaskbarButtons
{
    /// <summary>The VS Code taskbar button's bounds (physical pixels) if <paramref name="p"/> is over it.</summary>
    public static Native.RECT? VsCodeButtonAt(Native.POINT p)
    {
        try
        {
            // Start from the taskbar window itself (not FromPoint: on Windows 11 that stops at the taskbar's host
            // window, and while our popup is open it would return the popup), then find the button under the point.
            nint bar = TaskbarAt(p);
            if (bar == 0) return null;
            var e = AutomationElement.FromHandle(bar);
            var buttons = e.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons)
            {
                var c = button.Current;
                var b = c.BoundingRectangle;
                if (b.IsEmpty || !b.Contains(p.X, p.Y)) continue;
                if (!IsVsCode(c.AutomationId, c.Name)) return null;
                return new Native.RECT { Left = (int)b.Left, Top = (int)b.Top, Right = (int)b.Right, Bottom = (int)b.Bottom };
            }
        }
        catch (ElementNotAvailableException) { }
        catch (COMException) { }
        catch (InvalidOperationException) { }
        return null;
    }

    private static nint TaskbarAt(Native.POINT p)
    {
        nint main = Native.FindWindow("Shell_TrayWnd", null);
        if (main != 0 && Native.GetWindowRect(main, out var r) && r.Contains(p)) return main;
        nint h = 0;
        while ((h = Native.FindWindowEx(0, h, "Shell_SecondaryTrayWnd", null)) != 0)
            if (Native.GetWindowRect(h, out var s) && s.Contains(p)) return h;
        return 0;
    }

    /// <summary>
    /// Windows 11 gives taskbar buttons AutomationId "Appid: &lt;AppUserModelID&gt;"; VS Code's is Microsoft.VisualStudioCode.
    /// The name check covers older taskbars ("Visual Studio Code - 3 running windows") and excludes Insiders.
    /// </summary>
    internal static bool IsVsCode(string? automationId, string? name)
    {
        if (automationId is { Length: > 0 } a && a.StartsWith("Appid: ", StringComparison.Ordinal))
            return a.Contains("VisualStudioCode", StringComparison.OrdinalIgnoreCase) && !a.Contains("Insiders", StringComparison.OrdinalIgnoreCase);
        name ??= "";
        return name == "Visual Studio Code" ||
               (name.StartsWith("Visual Studio Code - ", StringComparison.Ordinal) && !name.StartsWith("Visual Studio Code - Insiders", StringComparison.Ordinal));
    }
}
