using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectPreviews.Core;
using ProjectPreviews.Platform;
using ProjectPreviews.Services;

namespace ProjectPreviews.UI;

public partial class SettingsWindow : Window
{
    private static readonly (string Name, string Hex)[] Palette =
    [
        ("Blue", "#3B82F6"), ("Violet", "#8B5CF6"), ("Pink", "#EC4899"), ("Orange", "#F97316"), ("Green", "#10B981"),
        ("Cyan", "#06B6D4"), ("Yellow", "#EAB308"), ("Red", "#EF4444"), ("Indigo", "#6366F1"), ("Teal", "#14B8A6"),
    ];

    private readonly CardView _sample;
    private bool _loading;
    private static App A => App.Current;

    public SettingsWindow()
    {
        InitializeComponent();
        // Fit high-scale/small screens (860 DIPs is taller than a 1080p screen at 200%); the content scrolls.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));
        _sample = new CardView(new CardModel("Sample Project", "index.ts", Color.FromRgb(0x3B, 0x82, 0xF6), null, null, "Sample card"),
            A.Settings.Blur, A.Settings.Darkness, liveBlur: true);
        _sample.Button.Focusable = false;
        _sample.SetImage(SyntheticEditor()); // captured previews are already blurred, so the sample uses its own image
        SampleHost.Child = _sample.Button;

        EnabledBox.Checked += (_, _) => Change(s => s.Enabled = true);
        EnabledBox.Unchecked += (_, _) => Change(s => s.Enabled = false);
        HoverBox.Checked += (_, _) => Change(s => s.HoverEnabled = true);
        HoverBox.Unchecked += (_, _) => Change(s => s.HoverEnabled = false);
        ShortcutBox.Checked += (_, _) => Change(s => s.ShortcutEnabled = true);
        ShortcutBox.Unchecked += (_, _) => Change(s => s.ShortcutEnabled = false);
        StartupBox.Checked += (_, _) => SetStartup(true);
        StartupBox.Unchecked += (_, _) => SetStartup(false);
        ThemeBox.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            Change(s => s.Theme = (ThemeChoice)ThemeBox.SelectedIndex, apply: false);
            ThemeManager.Apply(A.Settings.Theme);
        };
        BlurSlider.ValueChanged += (_, e) => { UpdateLookLabels(); if (!_loading) Change(s => s.Blur = Math.Round(e.NewValue), apply: false); };
        DarkSlider.ValueChanged += (_, e) => { UpdateLookLabels(); if (!_loading) Change(s => s.Darkness = Math.Round(e.NewValue) / 100, apply: false); };
        ShortcutText.PreviewKeyDown += OnShortcutKey;
        RefreshButton.Click += (_, _) => BuildProjects();
        InstallButton.Click += async (_, _) => await InstallCompanion();

        HoverNote.Text = "Shows the picker when the pointer rests on the VS Code button. While it's open it sits over that button, " +
                         "so Windows' own thumbnail popup doesn't open on top of it. No Explorer settings are changed.";
        Reload();
        BuildProjects();
    }

    /// <summary>Re-reads everything from the app (e.g. after Enable/Disable from the tray).</summary>
    public void Reload()
    {
        _loading = true;
        var s = A.Settings;
        EnabledBox.IsChecked = s.Enabled;
        HoverBox.IsChecked = s.HoverEnabled;
        ShortcutBox.IsChecked = s.ShortcutEnabled;
        StartupBox.IsChecked = Startup.IsEnabled;
        ShortcutText.Text = s.Shortcut;
        ThemeBox.SelectedIndex = (int)s.Theme;
        BlurSlider.Value = s.Blur;
        DarkSlider.Value = s.Darkness * 100;
        _loading = false;
        UpdateLookLabels();
        ShortcutNote.Text = A.ShortcutProblem ?? "";
        ShortcutNote.Visibility = A.ShortcutProblem is null ? Visibility.Collapsed : Visibility.Visible;
        ProblemBox.Visibility = A.SettingsProblem is null ? Visibility.Collapsed : Visibility.Visible;
        ProblemText.Text = A.SettingsProblem ?? "";
        var cw = A.Catalog.ClaudeWorkers;
        int n = cw.Current.Count;
        AliasSourceNote.Text = (cw.Found
            ? $"{n} alias{(n == 1 ? "" : "es")} read from Claude Workers ({cw.ConfigPath}); that file is never changed. "
            : "Claude Workers' alias file wasn't found, so folder names are used. ") +
            "An alias typed here overrides it for this exact folder or workspace path.";
        Footer.Text = $"Settings are stored in {SettingsStore.DefaultPath}";
    }

    private void Change(Action<AppSettings> edit, bool apply = true)
    {
        if (_loading) return;
        edit(A.Settings);
        A.SaveSettings();
        if (apply) A.ApplyEnabled();
        _sample.SetLook(A.Settings.Blur, A.Settings.Darkness);
        ShortcutNote.Text = A.ShortcutProblem ?? "";
        ShortcutNote.Visibility = A.ShortcutProblem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateLookLabels()
    {
        BlurLabel.Text = $"Blur: {BlurSlider.Value:0}";
        DarkLabel.Text = $"Darkness: {DarkSlider.Value:0}%";
    }

    private void SetStartup(bool on)
    {
        if (_loading) return;
        try { Startup.Set(on); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show(this, $"Couldn't change startup: {e.Message}", "Project Previews");
        }
        _loading = true;
        StartupBox.IsChecked = Startup.IsEnabled;
        _loading = false;
    }

    private void OnShortcutKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) return; // keep Tab navigation
        e.Handled = true;
        if (Shortcut.IsModifier(key)) return;
        uint mods = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= Native.MOD_CONTROL;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= Native.MOD_ALT;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= Native.MOD_SHIFT;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= Native.MOD_WIN;
        if (mods == 0 || mods == Native.MOD_SHIFT)
        {
            ShortcutNote.Text = "Include Ctrl, Alt or Win so the shortcut doesn't take over normal typing.";
            ShortcutNote.Visibility = Visibility.Visible;
            return;
        }
        string text = Shortcut.Format(mods, key);
        if (Shortcut.Parse(text) is null) return;
        ShortcutText.Text = text;
        Change(s => s.Shortcut = text);
    }

    // ---------- projects ----------

    private void BuildProjects()
    {
        ProjectRows.Children.Clear();
        var windows = A.Catalog.Snapshot();
        var claude = A.Catalog.ClaudeWorkers.Current;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in windows.GroupBy(w => w.Identity.Key ?? "name:" + w.Identity.Name, StringComparer.OrdinalIgnoreCase))
        {
            var w = g.First();
            seen.Add(g.Key);
            ProjectRows.Children.Add(Row(w.Identity, g.Count(), claude));
        }
        foreach (var (key, pref) in A.Settings.Projects.Where(p => !seen.Contains(p.Key)).OrderBy(p => p.Value.Path ?? p.Key))
        {
            string path = pref.Path ?? key;
            bool ws = path.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase);
            var id = new ProjectIdentity(ws ? ProjectKind.Workspace : ProjectKind.Folder, key, path,
                ws ? Path.GetFileNameWithoutExtension(path) : CompanionReport.LastSegment(path), true);
            ProjectRows.Children.Add(Row(id, 0, claude));
        }
        if (ProjectRows.Children.Count == 0)
            ProjectRows.Children.Add(new TextBlock { Text = "No VS Code windows are open.", Style = (Style)FindResource("Note") });

        int total = windows.Count, reporting = windows.Count(w => w.FromCompanion);
        bool installed = CompanionInstalled();
        CompanionStatus.Text = !installed
            ? "Not installed. Without it, projects are identified from window titles, and an alias can only be set when the folder is unambiguous."
            : total == 0 ? "Installed. No VS Code windows are open right now."
            : $"Installed. {reporting} of {total} open window{(total == 1 ? "" : "s")} identified through it" +
              (reporting < total ? " (windows opened before installing pick it up within a few seconds; if not, reload them)." : ".");
        InstallButton.Content = installed ? "Reinstall companion extension" : "Install companion extension";
    }

    private FrameworkElement Row(ProjectIdentity id, int openCount, IReadOnlyDictionary<string, string> claude)
    {
        var prefs = A.Settings.Projects;
        string? key = id.Key;
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var color = new ComboBox { Width = 104, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top, IsEnabled = key is not null };
        string autoHex = Aliases.DefaultAccent(key ?? id.Name);
        color.Items.Add(Swatch("Auto", autoHex, auto: true));
        foreach (var (n, hex) in Palette) color.Items.Add(Swatch(n, hex, auto: false));
        string? current = key is not null && prefs.TryGetValue(key, out var p0) ? p0.Color : null;
        color.SelectedIndex = current is null ? 0 : Math.Max(0, Array.FindIndex(Palette, c => c.Hex.Equals(current, StringComparison.OrdinalIgnoreCase)) + 1);

        var alias = new TextBox { IsEnabled = key is not null, Text = key is not null && prefs.TryGetValue(key, out var p1) ? p1.Alias ?? "" : "" };
        var note = new TextBlock { Style = (Style)FindResource("Note"), Margin = new Thickness(2, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        var reset = new Button { Content = "Reset", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4), VerticalAlignment = VerticalAlignment.Top };

        string label = id.Kind == ProjectKind.Empty ? "Empty window" : id.Name;
        AutomationProperties.SetName(alias, $"Alias for {label}");
        AutomationProperties.SetName(color, $"Accent color for {label}");
        AutomationProperties.SetName(reset, $"Reset alias and color for {label}");

        void Refresh()
        {
            var (shown, source) = Aliases.Resolve(id, prefs, claude);
            string from = source switch
            {
                AliasSource.Override => "your alias",
                AliasSource.ClaudeWorkers => "Claude Workers alias",
                AliasSource.ProjectName => id.Kind == ProjectKind.Workspace ? "workspace name" : "folder name",
                _ => id.Kind == ProjectKind.Empty ? "default" : "best guess",
            };
            string where = id.Path ?? (key is null ? "path unknown" : key);
            string open = openCount switch { 0 => " · not open", 1 => "", _ => $" · {openCount} windows" };
            note.Text = key is null
                ? $"Shown as “{shown}” ({from}) · {(id.Kind == ProjectKind.Empty ? "no folder open" : "exact path unknown, so no alias can be saved; the companion extension fixes this")}{open}"
                : $"Shown as “{shown}” ({from}) · {where}{open}";
            note.ToolTip = id.Path;
            reset.IsEnabled = key is not null && prefs.ContainsKey(key);
        }

        void Commit(string? newAlias, string? newColor)
        {
            if (key is null) return;
            var p = prefs.TryGetValue(key, out var e) ? e : new ProjectPref();
            p.Alias = string.IsNullOrWhiteSpace(newAlias) ? null : newAlias.Trim();
            p.Color = newColor;
            p.Path = id.Path ?? p.Path;
            if (p.Alias is null && p.Color is null) prefs.Remove(key); else prefs[key] = p;
            A.SaveSettings();
            Refresh();
        }

        string? SelectedColor() => color.SelectedIndex <= 0 ? null : Palette[color.SelectedIndex - 1].Hex;
        alias.LostFocus += (_, _) => Commit(alias.Text, SelectedColor());
        alias.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(alias.Text, SelectedColor()); };
        color.SelectionChanged += (_, _) => Commit(alias.Text, SelectedColor());
        reset.Click += (_, _) =>
        {
            if (key is null) return;
            prefs.Remove(key);
            A.SaveSettings();
            alias.Text = "";
            color.SelectedIndex = 0;
            Refresh();
        };
        Refresh();

        var mid = new StackPanel();
        mid.Children.Add(alias);
        mid.Children.Add(note);
        Grid.SetColumn(mid, 1);
        Grid.SetColumn(reset, 2);
        grid.Children.Add(color);
        grid.Children.Add(mid);
        grid.Children.Add(reset);
        return grid;
    }

    private static FrameworkElement Swatch(string name, string hex, bool auto)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(CardView.ParseColor(hex)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), BorderThickness = new Thickness(1),
        });
        if (auto) sp.Children.Add(new TextBlock { Text = "Auto", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        AutomationProperties.SetName(sp, auto ? "Automatic color" : name);
        sp.ToolTip = auto ? "Automatic" : name;
        return sp;
    }

    // ---------- companion ----------

    internal static string VsixPath => Path.Combine(AppContext.BaseDirectory, "companion", "project-previews-companion.vsix");

    private static bool CompanionInstalled()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        try { return Directory.EnumerateDirectories(dir, "projectpreviews.project-previews-companion-*").Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? FindCodeCli()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\bin\code.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft VS Code\bin\code.cmd"),
        ];
        return candidates.FirstOrDefault(File.Exists) ??
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => Path.Combine(d.Trim(), "code.cmd")).FirstOrDefault(File.Exists);
    }

    private async Task InstallCompanion()
    {
        string? cli = FindCodeCli();
        if (!File.Exists(VsixPath)) { CompanionStatus.Text = $"The extension package is missing ({VsixPath})."; return; }
        if (cli is null) { CompanionStatus.Text = "Couldn't find VS Code's \"code\" command. In VS Code, run \"Extensions: Install from VSIX…\" and pick " + VsixPath; return; }
        InstallButton.IsEnabled = false;
        CompanionStatus.Text = "Installing…";
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/d /c \"\"{cli}\" --install-extension \"{VsixPath}\" --force\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            string output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            await Task.Delay(1500); // give running windows a moment to start it
            BuildProjects();
            if (p.ExitCode != 0) CompanionStatus.Text = "Install failed: " + output.Trim();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            CompanionStatus.Text = "Install failed: " + e.Message;
        }
        finally { InstallButton.IsEnabled = true; }
    }

    /// <summary>A stand-in "editor" image so blur and darkness have something to act on when no capture exists yet.</summary>
    private static BitmapSource SyntheticEditor()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), null, new Rect(0, 0, 560, 320));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)), null, new Rect(0, 0, 120, 320));
            var rnd = new Random(7);
            Color[] syntax = [Color.FromRgb(0x56, 0x9C, 0xD6), Color.FromRgb(0xCE, 0x91, 0x78), Color.FromRgb(0x4E, 0xC9, 0xB0), Color.FromRgb(0xDC, 0xDC, 0xAA), Color.FromRgb(0x9C, 0xDC, 0xFE)];
            for (int y = 16; y < 310; y += 14)
            {
                double x = 140 + rnd.Next(0, 4) * 16;
                for (int k = rnd.Next(1, 5); k > 0; k--)
                {
                    double w = rnd.Next(20, 90);
                    dc.DrawRoundedRectangle(new SolidColorBrush(syntax[rnd.Next(syntax.Length)]), null, new Rect(x, y, w, 6), 3, 3);
                    x += w + 8;
                }
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5E)), null, new Rect(14, y, rnd.Next(40, 90), 6), 3, 3);
            }
        }
        var bmp = new RenderTargetBitmap(560, 320, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }
}
