using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using ProjectPreviews.Core;

namespace ProjectPreviews.UI;

/// <summary>
/// Light/dark/system. Standard controls (menus, tooltips, sliders, combo boxes) use .NET 9's Fluent ThemeMode;
/// the popup and cards use the brushes below, looked up with DynamicResource so they switch live.
/// </summary>
public static class ThemeManager
{
    private static ThemeChoice _choice = ThemeChoice.System;
    private static bool _hooked;

    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    /// <summary>Animations are skipped when Windows' "Animation effects" is off.</summary>
    public static bool MotionAllowed => SystemParameters.ClientAreaAnimation;

    public static void Apply(ThemeChoice choice)
    {
        _choice = choice;
        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(_choice));
            };
        }
        IsDark = choice == ThemeChoice.Dark || (choice == ThemeChoice.System && SystemUsesDark());
        var app = Application.Current;
        if (app is null) return;
        var mode = choice switch { ThemeChoice.Light => ThemeMode.Light, ThemeChoice.Dark => ThemeMode.Dark, _ => ThemeMode.System };
        if (app.ThemeMode != mode) app.ThemeMode = mode;

        var r = app.Resources;
        if (SystemParameters.HighContrast)
        {
            Set(r, "PopupBg", SystemColors.WindowColor);
            Set(r, "PopupBorder", SystemColors.WindowTextColor);
            Set(r, "PopupText", SystemColors.WindowTextColor);
            Set(r, "PopupSubtle", SystemColors.WindowTextColor);
            Set(r, "FocusRing", SystemColors.HighlightColor);
            Set(r, "HoverRing", SystemColors.HotTrackColor);
            Set(r, "TabFill", Color.FromArgb(0x40, SystemColors.HighlightColor.R, SystemColors.HighlightColor.G, SystemColors.HighlightColor.B));
            Set(r, "SurfaceBg", SystemColors.WindowColor);
            Set(r, "SurfaceCard", SystemColors.WindowColor);
            Set(r, "SurfaceBorder", SystemColors.WindowTextColor);
        }
        else if (IsDark)
        {
            Set(r, "PopupBg", Color.FromArgb(0xF2, 0x1E, 0x1F, 0x23));
            Set(r, "PopupBorder", Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            Set(r, "PopupText", Color.FromRgb(0xF3, 0xF4, 0xF6));
            Set(r, "PopupSubtle", Color.FromRgb(0xA1, 0xA7, 0xB3));
            Set(r, "FocusRing", Color.FromRgb(0x7C, 0xB4, 0xFF));
            Set(r, "HoverRing", Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
            Set(r, "TabFill", Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            Set(r, "SurfaceBg", Color.FromRgb(0x1C, 0x1C, 0x1F));
            Set(r, "SurfaceCard", Color.FromRgb(0x26, 0x27, 0x2B));
            Set(r, "SurfaceBorder", Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            Set(r, "PopupBg", Color.FromArgb(0xF5, 0xF6, 0xF7, 0xF9));
            Set(r, "PopupBorder", Color.FromArgb(0x24, 0x00, 0x00, 0x00));
            Set(r, "PopupText", Color.FromRgb(0x1F, 0x23, 0x2B));
            Set(r, "PopupSubtle", Color.FromRgb(0x5B, 0x63, 0x70));
            Set(r, "FocusRing", Color.FromRgb(0x1D, 0x5F, 0xD6));
            Set(r, "HoverRing", Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
            Set(r, "TabFill", Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            Set(r, "SurfaceBg", Color.FromRgb(0xF3, 0xF4, 0xF6));
            Set(r, "SurfaceCard", Color.FromRgb(0xFF, 0xFF, 0xFF));
            Set(r, "SurfaceBorder", Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
        }
        Changed?.Invoke();
    }

    private static void Set(ResourceDictionary r, string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        r[key] = b;
    }

    public static bool SystemUsesDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}
