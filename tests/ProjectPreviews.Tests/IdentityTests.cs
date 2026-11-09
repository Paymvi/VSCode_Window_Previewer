using System.IO;
using ProjectPreviews.Core;
using ProjectPreviews.Platform;
using ProjectPreviews.Services;

namespace ProjectPreviews.Tests;

public class AliasTests
{
    private static readonly Dictionary<string, string> Claude = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Toah_Nipi_Website"] = "Website",
    };

    private static ProjectIdentity Folder(string path) =>
        new(ProjectKind.Folder, Aliases.NormalizeKey(path), path, Path.GetFileName(path), true);

    [Fact]
    public void Precedence_is_override_then_claude_workers_then_readable_name()
    {
        var id = Folder(@"C:\work\Toah_Nipi_Website");
        var none = new Dictionary<string, ProjectPref>();
        Assert.Equal(("Website", AliasSource.ClaudeWorkers), Aliases.Resolve(id, none, Claude));

        var prefs = new Dictionary<string, ProjectPref> { [id.Key!] = new() { Alias = "Marketing site" } };
        Assert.Equal(("Marketing site", AliasSource.Override), Aliases.Resolve(id, prefs, Claude));

        var plain = Folder(@"C:\work\My_Other_App");
        Assert.Equal(("My Other App", AliasSource.ProjectName), Aliases.Resolve(plain, none, Claude));
    }

    [Fact]
    public void Override_is_per_full_path_so_identically_named_folders_differ()
    {
        var a = Folder(@"C:\clients\Acme\Toah_Nipi_Website");
        var b = Folder(@"D:\archive\Toah_Nipi_Website");
        var prefs = new Dictionary<string, ProjectPref> { [a.Key!] = new() { Alias = "Acme site" } };
        Assert.Equal("Acme site", Aliases.Resolve(a, prefs, Claude).Alias);
        Assert.Equal("Website", Aliases.Resolve(b, prefs, Claude).Alias);
    }

    [Fact]
    public void Override_never_attaches_to_a_name_only_guess()
    {
        var guess = new ProjectIdentity(ProjectKind.Unknown, null, null, "Toah_Nipi_Website", false);
        var prefs = new Dictionary<string, ProjectPref> { [Aliases.NormalizeKey(@"C:\x\Toah_Nipi_Website")] = new() { Alias = "Nope" } };
        Assert.Equal("Website", Aliases.Resolve(guess, prefs, Claude).Alias);
        var unknown = new ProjectIdentity(ProjectKind.Unknown, null, null, "Something", false);
        Assert.Equal(AliasSource.Fallback, Aliases.Resolve(unknown, prefs, Claude).Source);
    }

    [Fact]
    public void Blank_override_falls_through_and_empty_window_has_a_label()
    {
        var id = Folder(@"C:\work\Toah_Nipi_Website");
        var prefs = new Dictionary<string, ProjectPref> { [id.Key!] = new() { Alias = "   ", Color = "#3B82F6" } };
        Assert.Equal("Website", Aliases.Resolve(id, prefs, Claude).Alias);
        Assert.Equal("Empty window", Aliases.Resolve(ProjectIdentity.Empty, prefs, Claude).Alias);
    }

    [Theory]
    [InlineData(@"C:\Users\Me\Proj\", @"c:\users\me\proj")]
    [InlineData(@"c:/users/me/proj", @"c:\users\me\proj")]
    [InlineData("file:///c%3A/Users/Me/Proj", @"c:\users\me\proj")]
    [InlineData("file:///c:/Users/Me/My%20Proj", @"c:\users\me\my proj")]
    [InlineData("file://server/share/Proj", @"\\server\share\proj")]
    [InlineData(@"C:\", @"c:\")]
    [InlineData("vscode-remote://wsl+Ubuntu/home/me/proj/", "vscode-remote://wsl+ubuntu/home/me/proj")]
    public void NormalizeKey_makes_equivalent_paths_equal(string input, string expected) =>
        Assert.Equal(expected, Aliases.NormalizeKey(input));

    [Fact]
    public void Default_accent_is_stable_and_from_the_palette()
    {
        Assert.Equal(Aliases.DefaultAccent(@"c:\a\b"), Aliases.DefaultAccent(@"C:\A\B"));
        Assert.True(AppSettings.IsColor(Aliases.DefaultAccent("anything")));
    }
}

public class ClaudeWorkersTests
{
    [Fact]
    public void Parses_the_real_config_shape_case_insensitively()
    {
        const string json = """
            {
              "soundOnDone": true,
              "projectAliases": { "Toah_Nipi_Portal": "Portal", "Guest_Booking_Form": "Guest Form", "Bad": 5, "Empty": "  " },
              "phonePort": 48521
            }
            """;
        var a = ClaudeWorkersAliases.Parse(json);
        Assert.Equal("Portal", a["toah_nipi_portal"]);
        Assert.Equal("Guest Form", a["Guest_Booking_Form"]);
        Assert.False(a.ContainsKey("Bad"));
        Assert.False(a.ContainsKey("Empty"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "projectAliases": [] }""")]
    [InlineData("[]")]
    public void Bad_input_gives_no_aliases(string json) => Assert.Empty(ClaudeWorkersAliases.Parse(json));

    [Fact]
    public void Reads_file_without_changing_it_and_tolerates_a_missing_file()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(dir, "config.json");
        var reader = new ClaudeWorkersAliases(path);
        Assert.Empty(reader.Current);
        Assert.False(reader.Found);

        File.WriteAllText(path, """{ "projectAliases": { "A_B": "Alpha" } }""");
        var stamp = File.GetLastWriteTimeUtc(path);
        Assert.Equal("Alpha", reader.Current["A_B"]);
        Assert.True(reader.Found);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }
}

public class SettingsTests
{
    private static string TempFile() => Path.Combine(Directory.CreateTempSubdirectory().FullName, "settings.json");

    [Fact]
    public void Missing_file_gives_defaults_with_startup_independent_settings()
    {
        var s = SettingsStore.Load(TempFile(), out var problem);
        Assert.Null(problem);
        Assert.True(s.Enabled);
        Assert.True(s.HoverEnabled);
        Assert.Equal(ThemeChoice.System, s.Theme);
    }

    [Fact]
    public void Damaged_file_is_backed_up_not_overwritten()
    {
        string path = TempFile();
        File.WriteAllText(path, "{ \"Enabled\": tru");
        var s = SettingsStore.Load(path, out var problem);
        Assert.NotNull(problem);
        Assert.True(s.Enabled);
        var backup = Directory.GetFiles(Path.GetDirectoryName(path)!, "settings.corrupt-*.json").Single();
        Assert.Equal("{ \"Enabled\": tru", File.ReadAllText(backup));

        SettingsStore.Save(path, s); // now safe: the damaged original lives on in the backup
        Assert.True(SettingsStore.Load(path, out _).Enabled);
    }

    [Fact]
    public void Out_of_range_values_are_clamped_and_keys_normalized()
    {
        string path = TempFile();
        File.WriteAllText(path, """
            { "Blur": 900, "Darkness": -3, "Theme": "Dark", "Shortcut": "",
              "Projects": { "C:/Work/Site/": { "Alias": "Site", "Color": "red" }, "C:\\Empty": { "Alias": " " } } }
            """);
        var s = SettingsStore.Load(path, out var problem);
        Assert.Null(problem);
        Assert.Equal(40, s.Blur);
        Assert.Equal(0, s.Darkness);
        Assert.Equal(ThemeChoice.Dark, s.Theme);
        Assert.Equal("Ctrl+Alt+Space", s.Shortcut);
        var site = Assert.Single(s.Projects);
        Assert.Equal(@"c:\work\site", site.Key);
        Assert.Null(site.Value.Color); // "red" isn't #RRGGBB
    }

    [Fact]
    public void Save_and_load_round_trip_overrides()
    {
        string path = TempFile();
        var s = new AppSettings { Theme = ThemeChoice.Light, Blur = 7 };
        s.Projects[Aliases.NormalizeKey(@"C:\Work\Site")] = new ProjectPref { Alias = "Site", Color = "#10B981", Path = @"C:\Work\Site" };
        SettingsStore.Save(path, s);
        Assert.False(File.Exists(path + ".tmp"));
        var back = SettingsStore.Load(path, out _);
        Assert.Equal(ThemeChoice.Light, back.Theme);
        Assert.Equal(7, back.Blur);
        Assert.Equal("Site", back.Projects[@"c:\work\site"].Alias);
    }
}

public class TitleTests
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase) { "Project-Previews", "MdViewer" };

    [Theory]
    [InlineData("index.ts - Project-Previews - Visual Studio Code", "index.ts", "Project-Previews", false, true)]
    [InlineData("● index.ts - Project-Previews - Visual Studio Code", "index.ts", "Project-Previews", false, true)]
    [InlineData("Project-Previews - Visual Studio Code", null, "Project-Previews", false, true)]
    [InlineData("README.md (Working Tree) (README.md) - MdViewer - Visual Studio Code", "README.md (Working Tree) (README.md)", "MdViewer", false, true)]
    [InlineData("Welcome - demo (Workspace) - Visual Studio Code", "Welcome", "demo", true, true)]
    [InlineData("a.ts - Unlisted - Work - Visual Studio Code", "a.ts", "Unlisted", false, false)]
    [InlineData("[Extension Development Host] a.ts - MdViewer - Visual Studio Code", "a.ts", "MdViewer", false, true)]
    public void Parses_default_titles(string title, string? file, string root, bool workspace, bool confirmed)
    {
        var p = VsTitle.Parse(title, Known)!;
        Assert.Equal(file, p.ActiveFile);
        Assert.Equal(root, p.Root);
        Assert.Equal(workspace, p.IsWorkspace);
        Assert.Equal(confirmed, p.RootConfirmed);
    }

    [Fact]
    public void Empty_window_and_custom_titles()
    {
        Assert.Null(VsTitle.Parse("Visual Studio Code", Known)!.Root);
        Assert.Null(VsTitle.Parse("my custom title", Known));
    }

    [Fact]
    public void Title_only_identity_gets_a_path_only_when_unambiguous()
    {
        var folders = new List<string> { @"C:\a\Site", @"D:\b\Site", @"C:\a\Docs" };
        var names = folders.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        var (docs, file) = WindowCatalog.FromTitle("x.md - Docs - Visual Studio Code", folders, names!);
        Assert.Equal(@"c:\a\docs", docs.Key);
        Assert.Equal("x.md", file);
        var (site, _) = WindowCatalog.FromTitle("x.md - Site - Visual Studio Code", folders, names!);
        Assert.Null(site.Key); // two folders are called Site: don't guess
        Assert.Equal("Site", site.Name);
    }
}

public class CompanionTests
{
    private static CompanionReport R(string name, string? ws, string[] folders, string? file = null, long focusedAt = 0, bool focused = false, string id = "s") =>
        new(id, 100, name, ws, folders, file, focused, focusedAt, 0);

    [Fact]
    public void Identity_from_report()
    {
        var folder = R("Site", null, [@"c:\work\Site"]).ToIdentity();
        Assert.Equal((ProjectKind.Folder, @"c:\work\site", "Site"), (folder.Kind, folder.Key, folder.Name));

        var ws = R("demo (Workspace)", @"c:\work\demo.code-workspace", [@"c:\work\api", @"c:\work\web"]).ToIdentity();
        Assert.Equal((ProjectKind.Workspace, @"c:\work\demo.code-workspace", "demo"), (ws.Kind, ws.Key, ws.Name));

        // "Duplicate As Workspace in New Window" makes an untitled workspace with the same single folder: same project.
        var dup = R("Untitled (Workspace)", "untitled", [@"c:\work\Site"]).ToIdentity();
        Assert.Equal(folder.Key, dup.Key);

        var multi = R("Untitled (Workspace)", "untitled", [@"c:\a", @"c:\b"]).ToIdentity();
        Assert.Null(multi.Key);
        Assert.Equal(ProjectKind.Empty, R("", null, []).ToIdentity().Kind);
        Assert.Equal("proj", R("proj", null, ["vscode-remote://wsl%2Bubuntu/home/me/proj"]).ToIdentity().Name);
    }

    [Fact]
    public void Parse_rejects_incomplete_reports()
    {
        Assert.Null(CompanionReport.Parse("""{ "pid": 5 }"""));
        Assert.Null(CompanionReport.Parse("garbage"));
        var r = CompanionReport.Parse("""{"sessionId":"a","pid":7,"name":"X","folders":["c:\\x"],"activeFile":"a.ts","focused":true,"focusedAt":5}""")!;
        Assert.Equal(("a", 7, "a.ts", true, 5L), (r.SessionId, r.Pid, r.ActiveFile, r.Focused, r.FocusedAt));
    }

    [Fact]
    public void Focus_correlation_binds_identical_titles_correctly()
    {
        // Two windows whose titles are identical; only focus timing can tell them apart.
        var w1 = new WindowTitle(1, "a.ts - Site - Visual Studio Code");
        var w2 = new WindowTitle(2, "a.ts - Site - Visual Studio Code");
        var r1 = R("Site", null, [@"c:\x\Site"], "a.ts", focusedAt: 10_000, id: "one");
        var r2 = R("Site", null, [@"c:\y\Site"], "a.ts", focusedAt: 20_000, id: "two");
        var focus = new List<FocusEvent> { new(2, 9_980), new(1, 19_990) };
        var sticky = new Dictionary<nint, string>();
        var bound = Binder.Bind([w1, w2], [r1, r2], focus, sticky, 0);
        Assert.Equal("two", bound[1].SessionId);
        Assert.Equal("one", bound[2].SessionId);

        // Remembered after the focus events are gone, and dropped when the window disappears.
        bound = Binder.Bind([w1, w2], [r1, r2], [], sticky, 0);
        Assert.Equal("two", bound[1].SessionId);
        Binder.Bind([w1], [r1, r2], [], sticky, 0);
        Assert.False(sticky.ContainsKey(2));
    }

    [Fact]
    public void Stale_binding_is_dropped_when_the_title_contradicts_it()
    {
        // An empty window just opened LendLog: the old (empty) report is still around and was bound by focus.
        var w = new WindowTitle(1, "README.md - LendLog - Visual Studio Code");
        var oldEmpty = R("", null, [], null, id: "old");
        var lendLog = R("LendLog", null, [@"c:\LendLog"], "README.md", id: "new");
        var sticky = new Dictionary<nint, string> { [1] = "old" };
        var bound = Binder.Bind([w], [oldEmpty, lendLog], [], sticky, 0);
        Assert.Equal("new", bound[1].SessionId);

        // A custom window.title (no product name) can't contradict anything, so the binding is kept.
        var custom = new WindowTitle(2, "my custom title");
        var sticky2 = new Dictionary<nint, string> { [2] = "new" };
        Assert.Equal("new", Binder.Bind([custom], [lendLog], [], sticky2, 0)[2].SessionId);
    }

    [Fact]
    public void Title_fallback_requires_the_workspace_name()
    {
        var w = new WindowTitle(1, "README.md - MdViewer - Visual Studio Code");
        var right = R("MdViewer", null, [@"c:\MdViewer"], "README.md", id: "right");
        var wrong = R("Other", null, [@"c:\Other"], "README.md", id: "wrong");
        var bound = Binder.Bind([w], [wrong, right], [], [], 0);
        Assert.Equal("right", bound[1].SessionId);
        Assert.Empty(Binder.Bind([w], [wrong], [], [], 0));
    }

    [Fact]
    public void Focused_report_binds_to_the_foreground_window()
    {
        var a = new WindowTitle(1, "x - Site - Visual Studio Code");
        var b = new WindowTitle(2, "x - Site - Visual Studio Code");
        var r = R("Site", null, [@"c:\Site"], "x", focused: true, id: "f");
        var bound = Binder.Bind([a, b], [r], [], [], foreground: 2);
        Assert.Equal("f", bound[2].SessionId);
        Assert.False(bound.ContainsKey(1));
    }

    [Fact]
    public void Same_project_windows_get_badges_and_name_clashes_get_locations()
    {
        ProjectWindow W(nint h, string path, string alias) => new()
        {
            Hwnd = h, Title = "", Minimized = false, Alias = alias, Source = AliasSource.ProjectName, Accent = "#3B82F6",
            Identity = new ProjectIdentity(ProjectKind.Folder, Aliases.NormalizeKey(path), path, Path.GetFileName(path), true),
        };
        var list = new List<ProjectWindow> { W(1, @"C:\a\Site", "Site"), W(2, @"C:\a\Site", "Site"), W(3, @"D:\old\Site", "Site"), W(4, @"C:\Docs", "Docs") };
        WindowCatalog.Disambiguate(list);
        Assert.Equal((1, 2), (list[0].DuplicateIndex, list[0].DuplicateCount));
        Assert.Equal((2, 2), (list[1].DuplicateIndex, list[1].DuplicateCount));
        Assert.Equal(1, list[2].DuplicateCount);
        Assert.Equal("a", list[0].Location);
        Assert.Equal("old", list[2].Location);
        Assert.Null(list[3].Location);
    }
}

public class PlatformLogicTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", "Ctrl+Alt+Space")]
    [InlineData("alt + ctrl + p", "Ctrl+Alt+P")]
    [InlineData("Win+Shift+F12", "Shift+Win+F12")]
    public void Parses_shortcuts(string input, string normalized) => Assert.Equal(normalized, Shortcut.Parse(input)!.Value.Text);

    [Theory]
    [InlineData("Space")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Nonsense")]
    [InlineData("Ctrl+A+B")]
    [InlineData("")]
    public void Rejects_bad_shortcuts(string input) => Assert.Null(Shortcut.Parse(input));

    [Theory]
    [InlineData("Appid: Microsoft.VisualStudioCode", "Visual Studio Code - 4 running windows pinned", true)]
    [InlineData("Appid: Microsoft.VisualStudioCodeInsiders", "Visual Studio Code - Insiders", false)]
    [InlineData("Appid: MSEdge", "Visual Studio Code", false)]
    [InlineData("", "Visual Studio Code - 2 running windows", true)]
    [InlineData("", "Visual Studio Code - Insiders - 1 running window", false)]
    [InlineData(null, "Notepad", false)]
    public void Recognizes_the_vs_code_taskbar_button(string? id, string name, bool expected) =>
        Assert.Equal(expected, TaskbarButtons.IsVsCode(id, name));

    [Fact]
    public void Baked_blur_keeps_flat_areas_flat_and_spreads_detail()
    {
        const int w = 40, h = 20;
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 100; px[i + 3] = 255; }
        int center = (10 * w + 20) * 4;
        px[center] = px[center + 1] = px[center + 2] = 255;
        var src = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
        var outPx = new byte[px.Length];
        Capture.Blur(src, 6).CopyPixels(outPx, w * 4, 0);
        Assert.Equal(100, outPx[0]);                    // corner: clamped edges, no dark fringe
        Assert.True(outPx[center] is > 100 and < 255);  // the bright pixel was spread out
        Assert.True(outPx[center + 12] > 100);          // ...onto its neighbours
        Assert.Equal(255, outPx[3]);                    // alpha untouched
    }

    [Fact]
    public void Blank_captures_are_detected()
    {
        Assert.True(Capture.LooksBlank(new byte[4000]));
        var px = new byte[4000];
        px[2000] = 200;
        Assert.False(Capture.LooksBlank(px));
    }
}
