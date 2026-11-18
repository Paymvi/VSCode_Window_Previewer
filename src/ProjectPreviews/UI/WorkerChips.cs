using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ProjectPreviews.Core;
using ProjectPreviews.Services;

namespace ProjectPreviews.UI;

/// <summary>Worker chips on a card, in either style. Creatures are AI Status Indicator's own sprite sheet, not a copy.</summary>
public static class WorkerChips
{
    public const double CreatureW = 102, CreatureH = 36, IndicatorW = 120, IndicatorH = 20, Gap = 4, MoreW = 30;

    private static BitmapSource? _sheet;
    private static bool _sheetTried;
    private static readonly Dictionary<(int, int), BitmapSource> Cells = [];

    public static string SheetPath => System.IO.Path.Combine(WorkerSource.DefaultRoot, "workers.png");

    /// <summary>AI Status Indicator's workers.png (13 hats × 8 bodies per pose; 4 poses), or null when it isn't installed.</summary>
    public static BitmapSource? Sheet
    {
        get
        {
            if (_sheetTried) return _sheet;
            _sheetTried = true;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file open
                bi.UriSource = new Uri(SheetPath);
                bi.EndInit();
                bi.Freeze();
                _sheet = bi.PixelWidth == 13 * 16 && bi.PixelHeight == 4 * 8 * 16 ? bi : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or UriFormatException or ArgumentException) { _sheet = null; }
            return _sheet;
        }
    }

    /// <summary>Look for the sheet again (e.g. AI Status Indicator was installed meanwhile).</summary>
    public static void Reload() { _sheetTried = false; _sheet = null; Cells.Clear(); }

    // Same cell as WorkerArt.Draw (phone.cs): column look % 13, row pose * 8 + look / 13; pose per status.
    private static BitmapSource? Cell(int look, string state)
    {
        if (Sheet is not { } sheet) return null;
        int pose = state switch { WorkerSession.NeedsYou => 1, WorkerSession.Done => 2, WorkerSession.Error => 3, _ => 0 };
        if (Cells.TryGetValue((look, pose), out var c)) return c;
        var crop = new CroppedBitmap(sheet, new Int32Rect(look % 13 * 16, (pose * 8 + look / 13) * 16, 16, 16));
        crop.Freeze();
        return Cells[(look, pose)] = crop;
    }

    // ---------- motion: one shared transform, flipped twice a second (phone.css "bob": 1 px up every other half second) ----------

    private static readonly TranslateTransform Bob = new();
    private static DispatcherTimer? _bobTimer;

    /// <summary>Runs only while a card shows a working creature and Windows' animation effects are on.</summary>
    public static void Animate(bool on)
    {
        if (on && ThemeManager.MotionAllowed)
        {
            _bobTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Bob.Y = Bob.Y == 0 ? -2 : 0, Dispatcher.CurrentDispatcher);
            _bobTimer.Start();
        }
        else
        {
            _bobTimer?.Stop();
            Bob.Y = 0;
        }
    }

    public static bool WantsMotion(IEnumerable<WorkerChip> chips, WorkerStyle style) =>
        style == WorkerStyle.Creatures && Sheet is not null && chips.Any(c => c.Tone == WorkerTone.Working);

    // ---------- chips ----------

    public static Color ToneColor(WorkerTone t) => t switch
    {
        WorkerTone.Working => Color.FromRgb(0xE3, 0xB3, 0x41),
        WorkerTone.Attention or WorkerTone.Error => Color.FromRgb(0xF8, 0x51, 0x49),
        WorkerTone.Done => Color.FromRgb(0x3F, 0xB9, 0x50),
        _ => Color.FromRgb(0x8B, 0x94, 0x9E),
    };

    // Claude coral / Codex slate blue, the same body colours the creatures use.
    private static Color ProviderColor(WorkerProvider p) => p == WorkerProvider.Codex ? Color.FromRgb(0x7F, 0x97, 0xBD) : Color.FromRgb(0xD9, 0x77, 0x57);

    /// <summary>
    /// The strip for one card, bottom-centered, or null for no workers (no space taken). It's at most as tall as two
    /// indicator rows or one creature row, so the alias keeps at least one line; extra workers fold into "+N", whose
    /// tooltip lists them.
    /// </summary>
    public static FrameworkElement? Strip(IReadOnlyList<WorkerChip> chips, WorkerStyle style, out double height)
    {
        height = 0;
        if (chips.Count == 0) return null;
        bool creatures = style == WorkerStyle.Creatures && Sheet is not null;
        int max = creatures ? 2 : 4;
        int shown = chips.Count <= max ? chips.Count : creatures ? max : max - 1;
        var panel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 10, 10) };
        panel.MaxWidth = creatures ? 2 * (CreatureW + Gap) + MoreW + Gap : 2 * (IndicatorW + Gap);
        foreach (var c in chips.Take(shown)) panel.Children.Add(creatures ? Creature(c) : Indicator(c));
        if (shown < chips.Count)
        {
            var rest = chips.Skip(shown).ToList();
            var more = Chip(new TextBlock { Text = $"+{rest.Count}", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                MoreW, creatures ? CreatureH : IndicatorH, string.Join("\n\n", rest.Select(r => r.Tooltip)));
            panel.Children.Add(more);
        }
        int rows = creatures ? 1 : (int)Math.Ceiling(Math.Min(chips.Count, max) / 2.0);
        height = rows * ((creatures ? CreatureH : IndicatorH) + Gap) + 10;
        return panel;
    }

    // Fixed width for creatures; indicators size to their text, up to w, so two always fit per row.
    private static Border Chip(UIElement content, double w, double h, string tip, bool fit = false)
    {
        var b = new Border
        {
            Width = fit ? double.NaN : w, MaxWidth = w, Height = h, Margin = new Thickness(Gap / 2), CornerRadius = new CornerRadius(h >= CreatureH ? 8 : h / 2),
            Background = new SolidColorBrush(Color.FromArgb(0xA6, 0, 0, 0)), Child = content, ToolTip = tip,
        };
        AutomationProperties.SetName(b, tip.Replace('\n', ' '));
        return b;
    }

    private static TextBlock Line(WorkerChip c, double size, bool withStatus)
    {
        var t = new TextBlock { FontSize = size, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        t.Inlines.Add(new Run(c.Label) { FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Lighten(ProviderColor(c.Provider))) });
        if (withStatus) t.Inlines.Add(new Run(" · " + c.Status));
        return t;
    }

    private static Ellipse Dot(WorkerTone t, double size) => new()
    {
        Width = size, Height = size, Fill = new SolidColorBrush(ToneColor(t)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
    };

    private static Border Indicator(WorkerChip c)
    {
        var dock = new DockPanel { Margin = new Thickness(6, 0, 7, 0), LastChildFill = true };
        var dot = Dot(c.Tone, 8);
        DockPanel.SetDock(dot, Dock.Left);
        dock.Children.Add(dot);
        dock.Children.Add(Line(c, 11, withStatus: true));
        return Chip(dock, IndicatorW, IndicatorH, c.Tooltip, fit: true);
    }

    private static Border Creature(WorkerChip c)
    {
        var img = new Image { Source = Cell(c.Look, c.State), Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor); // crisp pixels
        if (c.Tone == WorkerTone.Working) img.RenderTransform = Bob;
        var status = new StackPanel { Orientation = Orientation.Horizontal };
        status.Children.Add(Dot(c.Tone, 7));
        status.Children.Add(new TextBlock { Text = c.Status, FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = CreatureW - 50 });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Line(c, 11, withStatus: false));
        text.Children.Add(status);
        var dock = new DockPanel();
        DockPanel.SetDock(img, Dock.Left);
        dock.Children.Add(img);
        dock.Children.Add(text);
        return Chip(dock, CreatureW, CreatureH, c.Tooltip);
    }

    private static Color Lighten(Color c) => Color.FromRgb((byte)(c.R + (255 - c.R) * 0.35), (byte)(c.G + (255 - c.G) * 0.35), (byte)(c.B + (255 - c.B) * 0.35));
}
