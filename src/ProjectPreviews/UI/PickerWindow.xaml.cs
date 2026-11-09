using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ProjectPreviews.Core;
using ProjectPreviews.Platform;
using ProjectPreviews.Services;

namespace ProjectPreviews.UI;

public enum PickerMode { Hover, Keyboard }

public partial class PickerWindow : Window
{
    private const double Gap = 10, ShadowPad = 24, Chrome = 6 + 10 + 22 + 6 + 2; // padding + header + border
    private static readonly double CellW = CardView.W + 12, CellH = CardView.H + 12;

    private readonly List<nint> _order = [];
    private readonly Dictionary<nint, CardView> _cards = [];
    private Native.RECT? _button;
    private nint _hwnd;

    public PickerWindow()
    {
        InitializeComponent();
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        // Software rendering: measured ~155 MB vs ~425 MB private with hardware rendering for this large layered
        // window. It's fast enough because nothing in the picker uses effects (previews are pre-blurred).
        HwndSource.FromHwnd(_hwnd).CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (IsVisible && Mode == PickerMode.Keyboard) DismissRequested?.Invoke(); };
        Tab.MouseDown += (_, e) =>
        {
            e.Handled = true;
            TabClicked?.Invoke(e.ChangedButton switch { MouseButton.Right => 1, MouseButton.Middle => 2, _ => 0 });
        };
    }

    public PickerMode Mode { get; private set; }
    public IReadOnlyList<nint> Order => _order;
    /// <summary>Regions (physical pixels) where the pointer counts as "on the popup": the panel, the bridge and the covered button.</summary>
    public Native.RECT[] HotRects { get; private set; } = [];
    public nint Handle => _hwnd;

    public event Action<nint>? CardChosen;
    public event Action<int>? TabClicked;
    public event Action? DismissRequested;

    /// <summary>
    /// Shows the cards. <paramref name="button"/> is the VS Code taskbar button in hover mode (popup anchors to it and
    /// covers it); null in keyboard mode (centered on the monitor with the cursor). Card order is kept stable while open.
    /// </summary>
    public void Present(IReadOnlyList<ProjectWindow> windows, AppSettings s, PickerMode mode, Native.RECT? button,
        IReadOnlyDictionary<nint, BitmapSource> images, bool keepOrder)
    {
        nint fgBefore = Native.GetForegroundWindow();
        bool wasVisible = IsVisible && !_warming;
        _warming = false;
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 1;
        Mode = mode;
        _button = button;
        if (!keepOrder) _order.Clear();
        _order.RemoveAll(h => windows.All(w => w.Hwnd != h));
        foreach (var w in windows) if (!_order.Contains(w.Hwnd)) _order.Add(w.Hwnd);
        var byHwnd = windows.ToDictionary(w => w.Hwnd);

        nint focused = _cards.FirstOrDefault(kv => kv.Value.Button.IsKeyboardFocused).Key;
        Build(_order.Select(h => byHwnd[h]), s, images);
        CardsPanel.Children.Clear();
        foreach (var h in _order) CardsPanel.Children.Add(_cards[h].Button);
        Header.Text = $"Visual Studio Code · {windows.Count} window{(windows.Count == 1 ? "" : "s")}";
        Hint.Text = mode == PickerMode.Keyboard ? "Arrows to move · Enter to switch · Esc to close" : "Click to switch";
        Header.ClearValue(TextBlock.ForegroundProperty);

        SetNoActivate(mode == PickerMode.Hover);
        Layout(windows.Count);

        if (!wasVisible)
        {
            Show();
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if (ThemeManager.MotionAllowed)
                Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }
        if (mode == PickerMode.Keyboard)
        {
            Activate();
            var target = focused != 0 && _cards.TryGetValue(focused, out var f) ? f : null;
            if (target is null && !wasVisible)
            {
                // Like Alt+Tab: start on the most recent VS Code window that isn't already in front.
                var first = _order.FirstOrDefault(h => h != fgBefore);
                target = first != 0 ? _cards[first] : _cards.Values.FirstOrDefault();
            }
            (target ?? _cards.Values.FirstOrDefault())?.Button.Focus();
        }
    }

    private readonly Dictionary<nint, string> _signatures = [];

    /// <summary>Builds card visuals ahead of time (or reuses unchanged ones), so showing is just layout. Safe while hidden.</summary>
    public void Prewarm(IReadOnlyList<ProjectWindow> windows, AppSettings s, IReadOnlyDictionary<nint, BitmapSource> images)
    {
        if (!IsVisible) Build(windows, s, images);
    }

    /// <summary>
    /// The first show of a window like this pays a one-time WPF setup cost (fonts, effect shaders, layered window)
    /// of ~0.5 s, long enough for Explorer's own preview to win the first hover. Paying it once, invisibly
    /// (off-screen, fully transparent, never activated), right after startup avoids that.
    /// </summary>
    public void WarmUp(IReadOnlyList<ProjectWindow> windows, AppSettings s, IReadOnlyDictionary<nint, BitmapSource> images)
    {
        if (IsVisible) return;
        Build(windows, s, images);
        CardsPanel.Children.Clear();
        foreach (var w in windows) CardsPanel.Children.Add(_cards[w.Hwnd].Button);
        if (CardsPanel.Children.Count == 0)
            CardsPanel.Children.Add(new CardView(new CardModel("Warm-up", "", Colors.SteelBlue, "x", "y", ""), s.Blur, s.Darkness).Button);
        SetNoActivate(true);
        _warming = true;
        Root.Opacity = 0;
        Native.SetWindowPos(_hwnd, 0, -32000, -32000, 800, 600, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Show();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (!_warming) return; // a real Present took over
            _warming = false;
            Hide();
            Root.Opacity = 1;
            CardsPanel.Children.Clear();
        });
    }

    private bool _warming;

    /// <summary>Visible to the user (the invisible warm-up pass doesn't count).</summary>
    public bool IsOpen => IsVisible && !_warming;

    private void Build(IEnumerable<ProjectWindow> windows, AppSettings s, IReadOnlyDictionary<nint, BitmapSource> images)
    {
        var keep = new HashSet<nint>();
        foreach (var w in windows)
        {
            nint h = w.Hwnd;
            keep.Add(h);
            string? left = w.Minimized ? "Minimized" : null;
            string? right = w.DuplicateCount > 1 ? $"{w.DuplicateIndex} of {w.DuplicateCount}" : null;
            string sig = string.Join('\u0001', w.Alias, w.Subtitle, w.Accent, left, right, s.Blur, s.Darkness, w.Identity.Path ?? w.Title);
            if (!_cards.TryGetValue(h, out var card) || _signatures[h] != sig)
            {
                string name = $"{w.Alias}, {w.Subtitle}{(left is null ? "" : ", minimized")}{(right is null ? "" : $", window {right}")}";
                card = new CardView(new CardModel(w.Alias, w.Subtitle, CardView.ParseColor(w.Accent), left, right, name), s.Blur, s.Darkness) { Hwnd = h };
                card.Button.Click += (_, _) => CardChosen?.Invoke(h);
                card.Button.ToolTip = w.Identity.Path ?? w.Title;
                _cards[h] = card;
                _signatures[h] = sig;
            }
            card.SetImage(!w.Minimized && images.TryGetValue(h, out var img) ? img : null);
        }
        foreach (var gone in _cards.Keys.Where(h => !keep.Contains(h)).ToList())
        {
            _cards.Remove(gone);
            _signatures.Remove(gone);
        }
    }

    public void SwitchToKeyboard()
    {
        Mode = PickerMode.Keyboard;
        SetNoActivate(false);
        Hint.Text = "Arrows to move · Enter to switch · Esc to close";
        Activate();
        _cards.Values.FirstOrDefault()?.Button.Focus();
    }

    public void SetImage(nint hwnd, BitmapSource image)
    {
        if (_cards.TryGetValue(hwnd, out var c)) c.SetImage(image);
    }

    public void ShowProblem(string text)
    {
        Header.Text = text;
        Header.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    }

    private void SetNoActivate(bool on)
    {
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        long want = on ? ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW : (ex & ~Native.WS_EX_NOACTIVATE) | Native.WS_EX_TOOLWINDOW;
        if (want != ex) Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, want);
    }

    private void Layout(int count)
    {
        Native.GetCursorPos(out var cursor);
        var rb = _button.GetValueOrDefault();
        nint mon = _button is not null ? Native.MonitorFromRect(ref rb, Native.MONITOR_DEFAULTTONEAREST) : Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
        var mi = Native.MonitorInfo(mon);
        var work = mi.rcWork;

        // Move onto the target monitor first so WPF adopts that monitor's DPI before we measure.
        Native.SetWindowPos(_hwnd, 0, work.Left, work.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double Px(double dip) => Math.Round(dip * s);

        // Which screen edge the button sits on decides where the panel goes.
        char edge = 'B';
        if (_button is { } b)
        {
            var m = mi.rcMonitor;
            int dl = b.Left - m.Left, dr = m.Right - b.Right, dt = b.Top - m.Top, db = m.Bottom - b.Bottom;
            int min = new[] { dl, dr, dt, db }.Min();
            edge = min == db ? 'B' : min == dt ? 'T' : min == dl ? 'L' : 'R';
        }

        // Available room for the panel, in physical pixels.
        int availL = work.Left, availT = work.Top, availR = work.Right, availB = work.Bottom;
        if (_button is { } bb)
        {
            if (edge == 'B') availB = Math.Min(availB, bb.Top);
            if (edge == 'T') availT = Math.Max(availT, bb.Bottom);
            if (edge == 'L') availL = Math.Max(availL, bb.Right);
            if (edge == 'R') availR = Math.Min(availR, bb.Left);
        }
        double margin = Gap + 6;
        double maxW = (availR - availL) / s - 2 * margin, maxH = (availB - availT) / s - 2 * margin;
        int cols = Math.Clamp((int)((maxW - 22) / CellW), 1, Math.Min(5, Math.Max(1, count)));
        int rows = (int)Math.Ceiling(count / (double)cols);
        int visibleRows = Math.Clamp((int)((maxH - Chrome) / CellH), 1, Math.Max(1, rows));
        bool scroll = rows > visibleRows;
        double panelW = cols * CellW + 22 + (scroll ? 14 : 0), panelH = Math.Max(1, visibleRows) * CellH + Chrome;
        CardsPanel.Width = cols * CellW;
        Scroller.Height = Math.Max(1, visibleRows) * CellH;

        int pw = (int)Px(panelW), ph = (int)Px(panelH), gap = (int)Px(Gap);
        int px, py;
        if (_button is { } a)
        {
            int cx = (a.Left + a.Right) / 2, cy = (a.Top + a.Bottom) / 2;
            (px, py) = edge switch
            {
                'T' => (cx - pw / 2, availT + gap),
                'L' => (availL + gap, cy - ph / 2),
                'R' => (availR - gap - pw, cy - ph / 2),
                _ => (cx - pw / 2, availB - gap - ph),
            };
        }
        else
        {
            (px, py) = ((work.Left + work.Right - pw) / 2, (work.Top + work.Bottom - ph) / 2);
        }
        int mg = (int)Px(6);
        px = Math.Clamp(px, availL + mg, Math.Max(availL + mg, availR - mg - pw));
        py = Math.Clamp(py, availT + mg, Math.Max(availT + mg, availB - mg - ph));
        var panel = new Native.RECT { Left = px, Top = py, Right = px + pw, Bottom = py + ph };

        var hot = new List<Native.RECT> { panel };
        Native.RECT? bridge = null;
        if (_button is { } t)
        {
            bridge = edge switch
            {
                'T' => new Native.RECT { Left = t.Left, Right = t.Right, Top = t.Top, Bottom = panel.Top + 2 },
                'L' => new Native.RECT { Left = t.Left, Right = panel.Left + 2, Top = t.Top, Bottom = t.Bottom },
                'R' => new Native.RECT { Left = panel.Right - 2, Right = t.Right, Top = t.Top, Bottom = t.Bottom },
                _ => new Native.RECT { Left = t.Left, Right = t.Right, Top = panel.Bottom - 2, Bottom = t.Bottom },
            };
            hot.Add(bridge.Value);
            hot.Add(t);
        }
        HotRects = hot.ToArray();

        // Window = everything we draw, plus room for the shadow.
        int sp = (int)Px(ShadowPad);
        int wl = hot.Min(r => r.Left) - sp, wt = hot.Min(r => r.Top) - sp, wr = hot.Max(r => r.Right) + sp, wb = hot.Max(r => r.Bottom) + sp;
        if (_button is { } tb)
        {
            // Never extend the window beyond the button on the taskbar side (it would sit over other taskbar content).
            if (edge == 'B') wb = tb.Bottom; if (edge == 'T') wt = tb.Top; if (edge == 'L') wl = tb.Left; if (edge == 'R') wr = tb.Right;
        }
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, wl, wt, wr - wl, wb - wt, Native.SWP_NOACTIVATE);

        void Place(FrameworkElement e, Native.RECT? r)
        {
            if (r is not { } v) { e.Visibility = Visibility.Collapsed; return; }
            e.Visibility = Visibility.Visible;
            Canvas.SetLeft(e, (v.Left - wl) / s);
            Canvas.SetTop(e, (v.Top - wt) / s);
            e.Width = v.Width / s;
            e.Height = v.Height / s;
        }
        Native.RECT Grow(Native.RECT r, int by, int down) => new() { Left = r.Left - by, Right = r.Right + by, Top = r.Top - by + down, Bottom = r.Bottom + by + down };
        Place(Panel, panel);
        Place(Shadow, Grow(panel, (int)Px(3), (int)Px(3)));
        Place(ShadowFar, Grow(panel, (int)Px(8), (int)Px(7)));
        Place(Bridge, bridge);
        Place(Tab, _button);
        Root.Width = (wr - wl) / s;
        Root.Height = (wb - wt) / s;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; DismissRequested?.Invoke(); }
        else if (e.Key == Key.Enter && Keyboard.FocusedElement is Button b && _cards.FirstOrDefault(kv => kv.Value.Button == b) is { Key: not 0 } hit)
        {
            e.Handled = true;
            CardChosen?.Invoke(hit.Key);
        }
        else if (e.Key is Key.Home or Key.End && _order.Count > 0)
        {
            e.Handled = true;
            _cards[e.Key == Key.Home ? _order[0] : _order[^1]].Button.Focus();
        }
    }
}
