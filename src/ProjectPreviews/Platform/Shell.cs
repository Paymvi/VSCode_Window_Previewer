using System.Windows.Input;
using Microsoft.Win32;

namespace ProjectPreviews.Platform;

public static class WindowSwitcher
{
    /// <summary>
    /// Restores (if minimized) and activates <paramref name="hwnd"/>; true only if it really is in front afterwards.
    /// Windows lets a process take the foreground when it received the last input (the click on our card or our
    /// hotkey); the empty SendInput refreshes that eligibility, the same technique PowerToys uses.
    /// </summary>
    public static bool Activate(nint hwnd)
    {
        if (!Native.IsWindow(hwnd)) return false;
        Native.SendInput(1, [new Native.INPUT { type = 0 }], System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        Native.SetForegroundWindow(hwnd);
        Native.BringWindowToTop(hwnd);
        for (int i = 0; i < 12 && Native.GetForegroundWindow() != hwnd; i++) System.Threading.Thread.Sleep(15);
        return Native.GetForegroundWindow() == hwnd;
    }
}

public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "ProjectPreviews";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string s && s.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
        else if (k.GetValue(Name) is not null) k.DeleteValue(Name);
    }
}

public readonly record struct Shortcut(uint Modifiers, uint VirtualKey, string Text)
{
    /// <summary>Parses "Ctrl+Alt+Space". Requires at least one modifier so a bare key can't be stolen system-wide.</summary>
    public static Shortcut? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Native.MOD_CONTROL; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win": case "windows": mods |= Native.MOD_WIN; break;
                default:
                    if (key != Key.None || !Enum.TryParse(raw, true, out key) || IsModifier(key)) return null;
                    break;
            }
        }
        if (mods == 0 || key == Key.None) return null;
        return new Shortcut(mods, (uint)KeyInterop.VirtualKeyFromKey(key), Format(mods, key));
    }

    public static string Format(uint mods, Key key)
    {
        var parts = new List<string>();
        if ((mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & Native.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
        or Key.RightShift or Key.LWin or Key.RWin or Key.System;
}
