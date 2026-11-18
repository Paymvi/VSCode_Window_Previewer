using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ProjectPreviews.UI;

public sealed record CardModel(string Alias, string Subtitle, Color Accent, string? BadgeLeft, string? BadgeRight, string AccessibleName);

/// <summary>A window card: blurred, darkened preview behind a large centered alias. Built in code so the popup and the settings sample share it.</summary>
public sealed class CardView
{
    public const double W = 272, H = 156, Radius = 12;

    private readonly Image _image;
    private readonly Rectangle _shade;
    private readonly BlurEffect _blur;
    private readonly Border _fallback;

    public Button Button { get; }
    public nint Hwnd { get; init; }

    /// <param name="liveBlur">
    /// True: blur with a BlurEffect (the settings sample, so the slider responds instantly). False: the image is
    /// already blurred (<see cref="Platform.Capture.Blur"/>), which keeps the software-rendered picker fast.
    /// </param>
    /// <param name="cardScale">Scales the whole card (preview, text, badges) proportionally.</param>
    /// <param name="textScale">Scales only the centered alias and subtitle, on top of <paramref name="cardScale"/>.</param>
    public CardView(CardModel m, double blur, double darkness, bool liveBlur = false, double cardScale = 1, double textScale = 1, Action? onClose = null)
    {
        _liveBlur = liveBlur;
        _blur = new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        _image = new Image { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
        if (liveBlur)
        {
            _image.Effect = blur > 0 ? _blur : null;
            // Slight zoom hides the soft, see-through edge a live blur leaves around the image.
            _image.RenderTransformOrigin = new Point(0.5, 0.5);
            _image.RenderTransform = new ScaleTransform(1.12, 1.12);
        }
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _shade = new Rectangle { Fill = Brushes.Black, Opacity = darkness, Visibility = Visibility.Collapsed };

        var deep = Color.FromRgb(0x14, 0x16, 0x1B);
        _fallback = new Border
        {
            Background = new LinearGradientBrush(Mix(m.Accent, deep, 0.62), Mix(m.Accent, deep, 0.88), new Point(0, 0), new Point(1, 1)),
        };

        // Text sits between the badge rows (top/bottom 30), so it never runs under a badge; the alias gets as many
        // lines (max 2) as fit above the subtitle, and the last one ends in an ellipsis.
        const double textPadV = 30;
        double lineH = 31 * textScale, subtitleH = (13 * 1.34 + 4) * textScale;
        int lines = Math.Clamp((int)((H - 2 * textPadV - subtitleH) / lineH), 1, 2);
        TextBlock AliasText(Brush fg) => new()
        {
            Text = m.Alias, FontSize = 25 * textScale, FontWeight = FontWeights.SemiBold, Foreground = fg,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = lines * lineH + 1, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = lineH,
        };
        _lineH = lineH; _subtitleH = subtitleH;
        var alias = AliasText(Brushes.White);
        // A static offset copy instead of a DropShadowEffect: same legibility on bright previews, no per-frame effect cost.
        var aliasShadow = AliasText(new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)));
        aliasShadow.RenderTransform = new TranslateTransform(0, 1.5);
        _alias = alias; _aliasShadow = aliasShadow;
        var aliasStack = new Grid();
        aliasStack.Children.Add(aliasShadow);
        aliasStack.Children.Add(alias);
        var subtitle = new TextBlock
        {
            Text = m.Subtitle, FontSize = 13 * textScale, Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0),
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, textPadV, 16, textPadV) };
        text.Children.Add(aliasStack);
        text.Children.Add(subtitle);
        _text = text;

        var accentBar = new Rectangle { Height = 4, VerticalAlignment = VerticalAlignment.Bottom, Fill = new SolidColorBrush(m.Accent) };

        var grid = _grid = new Grid { Width = W, Height = H, Clip = new RectangleGeometry(new Rect(0, 0, W, H), Radius, Radius) };
        grid.Children.Add(_fallback);
        grid.Children.Add(_image);
        grid.Children.Add(_shade);
        grid.Children.Add(text);
        grid.Children.Add(accentBar);
        if (m.BadgeLeft is not null) grid.Children.Add(Badge(m.BadgeLeft, HorizontalAlignment.Left));
        var right = m.BadgeRight is null ? null : Badge(m.BadgeRight, HorizontalAlignment.Right);
        if (right is not null) grid.Children.Add(right);
        if (onClose is not null)
        {
            var x = CloseButton(m.Alias);
            x.Click += (_, e) => { e.Handled = true; onClose(); };
            grid.Children.Add(x);
            if (right is not null) right.Margin = new Thickness(10, 10, 40, 10); // keep "1 of 2" clear of the X
        }

        Button = new Button
        {
            Content = grid, Width = W, Height = H, Margin = new Thickness(6), // Margin isn't scaled by LayoutTransform
            LayoutTransform = new ScaleTransform(cardScale, cardScale),
            Style = (Style)Application.Current.FindResource("CardButton"),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        AutomationProperties.SetName(Button, _name = m.AccessibleName);
        Button.ToolTip = null;
        if (ThemeManager.MotionAllowed)
        {
            Button.MouseEnter += (_, _) => Scale(1.025);
            Button.MouseLeave += (_, _) => Scale(1.0);
        }
    }

    public void SetImage(BitmapSource? image)
    {
        _image.Source = image;
        _image.Visibility = _shade.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetLook(double blur, double darkness)
    {
        _blur.Radius = blur;
        if (_liveBlur) _image.Effect = blur > 0 ? _blur : null;
        _shade.Opacity = darkness;
    }

    private readonly bool _liveBlur;
    private readonly Grid _grid;
    private readonly StackPanel _text;
    private readonly TextBlock _alias, _aliasShadow;
    private readonly double _lineH, _subtitleH;
    private readonly string _name;
    private FrameworkElement? _workers;
    private string _workerSig = "";

    /// <summary>
    /// Worker chips along the bottom (none: no space taken). The alias gives up its second line when they need the
    /// room, so chips never cover the alias, subtitle or badges.
    /// </summary>
    public void SetWorkers(IReadOnlyList<Core.WorkerChip> chips, Core.WorkerStyle style)
    {
        string sig = style + "|" + string.Join(";", chips.Select(c => $"{c.Label}|{c.Status}|{c.Tone}|{c.Look}|{c.State}|{c.Tooltip}"));
        if (sig == _workerSig) return;
        _workerSig = sig;
        if (_workers is not null) _grid.Children.Remove(_workers);
        _workers = WorkerChips.Strip(chips, style, out double height);
        if (_workers is not null) _grid.Children.Add(_workers);
        double bottom = Math.Max(30, height + 4);
        int lines = Math.Clamp((int)((H - 30 - bottom - _subtitleH) / _lineH), 1, 2);
        _alias.MaxHeight = _aliasShadow.MaxHeight = lines * _lineH + 1;
        _text.Margin = new Thickness(16, 30, 16, bottom);
        AutomationProperties.SetName(Button, chips.Count == 0 ? _name : $"{_name}. AI workers: {string.Join(", ", chips.Select(c => $"{c.Label} {c.Status}"))}");
    }

    private void Scale(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(110)) { EasingFunction = new QuadraticEase() };
        var st = (ScaleTransform)Button.RenderTransform;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static Border Badge(string text, HorizontalAlignment side) => new()
    {
        HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10),
        Padding = new Thickness(8, 2, 8, 3), CornerRadius = new CornerRadius(9),
        Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = Brushes.White },
    };

    private static Button CloseButton(string alias)
    {
        var bg = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
            Child = new TextBlock { Text = "✕", FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        bg.MouseEnter += (_, _) => bg.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
        bg.MouseLeave += (_, _) => bg.Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0));
        // Not focusable: arrow keys keep moving between cards.
        var b = new Button
        {
            Content = bg, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8),
            Focusable = false, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Close window",
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
        };
        AutomationProperties.SetName(b, $"Close {alias} window");
        return b;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch (FormatException) { return Color.FromRgb(0x3B, 0x82, 0xF6); }
    }
}
