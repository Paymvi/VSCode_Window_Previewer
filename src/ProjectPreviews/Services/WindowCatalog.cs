using System.Text.Json;
using ProjectPreviews.Core;
using ProjectPreviews.Platform;

namespace ProjectPreviews.Services;

/// <summary>One VS Code window as the picker shows it.</summary>
public sealed class ProjectWindow
{
    public required nint Hwnd { get; init; }
    public required string Title { get; init; }
    public required bool Minimized { get; init; }
    public required ProjectIdentity Identity { get; init; }
    public string? ActiveFile { get; init; }
    public required string Alias { get; init; }
    public required AliasSource Source { get; init; }
    public required string Accent { get; init; }
    public bool FromCompanion { get; init; }
    /// <summary>1-based position among windows of the same project, and how many there are (for "2 of 3" badges).</summary>
    public int DuplicateIndex { get; set; }
    public int DuplicateCount { get; set; }

    /// <summary>Parent folder name, set only when different projects would otherwise show the same alias.</summary>
    public string? Location { get; set; }

    public string Subtitle
    {
        get
        {
            string s = ActiveFile is { Length: > 0 } f ? f
                : Identity.Certain || Identity.Kind != ProjectKind.Unknown ? "No file open" : "Project not identified";
            return Location is null ? s : $"{s} · {Location}";
        }
    }
}

/// <summary>
/// Finds VS Code windows and works out what each has open. Nothing here polls: windows are enumerated
/// when the picker or settings asks, and foreground changes arrive as WinEvents (out-of-context, no injection).
/// </summary>
public sealed class WindowCatalog : IDisposable
{
    public static string ReportDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectPreviews", "windows");

    private readonly ClaudeWorkersAliases _claude;
    private readonly Func<AppSettings> _settings;
    private readonly Dictionary<int, bool> _isCodePid = [];
    private readonly List<FocusEvent> _focus = [];
    private readonly Dictionary<nint, string> _sticky = [];
    private readonly Native.WinEventProc _proc; // kept alive: the hook holds a pointer to it
    private nint _hook;

    public WindowCatalog(ClaudeWorkersAliases claude, Func<AppSettings> settings)
    {
        _claude = claude;
        _settings = settings;
        _proc = OnForeground;
    }

    public ClaudeWorkersAliases ClaudeWorkers => _claude;
    public int CompanionWindowCount { get; private set; }

    public void StartTracking()
    {
        if (_hook == 0)
            _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, 0, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    public void StopTracking()
    {
        if (_hook != 0) Native.UnhookWinEvent(_hook);
        _hook = 0;
    }

    public void Dispose() => StopTracking();

    private void OnForeground(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == 0 || !IsCodeWindow(hwnd)) return;
        _focus.Add(new FocusEvent(hwnd, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        if (_focus.Count > 64) _focus.RemoveRange(0, _focus.Count - 64);
    }

    public bool IsCodeWindow(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out int pid);
        if (!_isCodePid.TryGetValue(pid, out bool code))
        {
            if (_isCodePid.Count > 512) _isCodePid.Clear();
            // VS Code (stable) only for v1. Insiders is "Code - Insiders.exe".
            _isCodePid[pid] = code = string.Equals(Path.GetFileName(Native.ImagePath(pid)), "Code.exe", StringComparison.OrdinalIgnoreCase);
        }
        return code;
    }

    /// <summary>Visible, uncloaked, top-level VS Code windows in z-order (most recently used first).</summary>
    public List<WindowTitle> EnumerateRaw()
    {
        var list = new List<WindowTitle>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GW_OWNER) != 0) return true;
            if ((Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.IsCloaked(h) || !IsCodeWindow(h)) return true;
            string title = Native.WindowText(h);
            if (title.Length > 0) list.Add(new WindowTitle(h, title));
            return true;
        }, 0);
        return list;
    }

    public List<ProjectWindow> Snapshot()
    {
        var raw = EnumerateRaw();
        var reports = LoadReports();
        CompanionWindowCount = reports.Count;
        var bound = Binder.Bind(raw, reports, _focus, _sticky, Native.GetForegroundWindow());
        var settings = _settings();
        var claude = _claude.Current;
        List<string> known = [];
        HashSet<string> knownNames = [];
        if (raw.Any(w => !bound.ContainsKey(w.Hwnd)))
        {
            known = KnownOpenFoldersCached();
            knownNames = known.Select(CompanionReport.LastSegment).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var result = new List<ProjectWindow>();
        foreach (var w in raw)
        {
            ProjectIdentity id;
            string? file;
            bool companion = bound.TryGetValue(w.Hwnd, out var rep);
            if (companion)
            {
                id = rep!.ToIdentity();
                file = rep.ActiveFile;
            }
            else
            {
                (id, file) = FromTitle(w.Title, known, knownNames);
            }
            var (alias, source) = Aliases.Resolve(id, settings.Projects, claude);
            string? color = id.Key is not null && settings.Projects.TryGetValue(id.Key, out var p) ? p.Color : null;
            result.Add(new ProjectWindow
            {
                Hwnd = w.Hwnd, Title = w.Title, Minimized = Native.IsIconic(w.Hwnd), Identity = id, ActiveFile = file,
                Alias = alias, Source = source, Accent = color ?? Aliases.DefaultAccent(id.Key ?? id.Name), FromCompanion = companion,
            });
        }
        Disambiguate(result);
        return result;
    }

    /// <summary>
    /// Windows of the same project get "1 of 2" badges; different projects that share an alias (e.g. two folders
    /// both named Website) get their parent folder in the subtitle.
    /// </summary>
    internal static void Disambiguate(List<ProjectWindow> windows)
    {
        foreach (var g in windows.GroupBy(r => (r.Identity.Key ?? "") + "|" + r.Alias, StringComparer.OrdinalIgnoreCase))
        {
            int i = 0, n = g.Count();
            foreach (var r in g) { r.DuplicateIndex = ++i; r.DuplicateCount = n; }
        }
        foreach (var g in windows.GroupBy(r => r.Alias, StringComparer.OrdinalIgnoreCase))
        {
            if (g.Select(r => r.Identity.Key ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) continue;
            foreach (var r in g)
                r.Location = r.Identity.Path is { } p && CompanionReport.LastSegment(Path.GetDirectoryName(p.TrimEnd('\\', '/')) ?? "") is { Length: > 0 } parent ? parent : null;
        }
    }

    /// <summary>Title-only identity. A full path (and so a local override) is attached only when exactly one open folder has that name.</summary>
    internal static (ProjectIdentity, string?) FromTitle(string title, IReadOnlyList<string> knownFolders, ICollection<string> knownNames)
    {
        var parts = VsTitle.Parse(title, knownNames);
        if (parts is null) return (new ProjectIdentity(ProjectKind.Unknown, null, null, title, false), null);
        if (parts.Root is null) return (ProjectIdentity.Empty, parts.ActiveFile);
        if (parts.IsWorkspace) return (new ProjectIdentity(ProjectKind.Workspace, null, null, parts.Root, false), parts.ActiveFile);
        var matches = knownFolders.Where(f => string.Equals(CompanionReport.LastSegment(f), parts.Root, StringComparison.OrdinalIgnoreCase))
            .Select(Aliases.NormalizeKey).Distinct().ToList();
        return matches.Count == 1 && parts.RootConfirmed
            ? (new ProjectIdentity(ProjectKind.Folder, matches[0], knownFolders.First(f => Aliases.NormalizeKey(f) == matches[0]), parts.Root, false), parts.ActiveFile)
            : (new ProjectIdentity(ProjectKind.Unknown, null, null, parts.Root, false), parts.ActiveFile);
    }

    private List<CompanionReport> LoadReports()
    {
        var list = new List<CompanionReport>();
        string[] files;
        try { files = Directory.GetFiles(ReportDir, "*.json"); } catch (IOException) { return list; } catch (UnauthorizedAccessException) { return list; }
        foreach (var f in files)
        {
            try
            {
                var r = CompanionReport.Parse(File.ReadAllText(f));
                if (r is null) continue;
                if (!ExtensionHostAlive(r.Pid))
                {
                    try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    continue;
                }
                list.Add(r);
            }
            catch (IOException) { } // mid-write; next snapshot will see it
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    // Image path is only readable while the process exists, so this is also the liveness check.
    private static bool ExtensionHostAlive(int pid) =>
        string.Equals(Path.GetFileName(Native.ImagePath(pid)), "Code.exe", StringComparison.OrdinalIgnoreCase);

    private DateTime _storageStamp;
    private List<string> _storageFolders = [];

    /// <summary>Folders VS Code itself lists as open (its storage.json), re-read only when it changes. Read-only.</summary>
    private List<string> KnownOpenFoldersCached()
    {
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Code\User\globalStorage\storage.json");
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return _storageFolders; }
        if (stamp != _storageStamp) { _storageFolders = KnownOpenFolders(file); _storageStamp = stamp; }
        return _storageFolders;
    }

    internal static List<string> KnownOpenFolders(string file)
    {
        var result = new List<string>();
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            if (root.TryGetProperty("windowsState", out var ws) && ws.TryGetProperty("openedWindows", out var opened) && opened.ValueKind == JsonValueKind.Array)
                foreach (var w in opened.EnumerateArray()) Add(w, "folder");
            if (root.TryGetProperty("backupWorkspaces", out var b) && b.TryGetProperty("folders", out var fl) && fl.ValueKind == JsonValueKind.Array)
                foreach (var w in fl.EnumerateArray()) Add(w, "folderUri");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        void Add(JsonElement e, string prop)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var u) && u.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) && uri.IsFile)
                result.Add(Aliases.FileUriToPath(uri));
        }
    }
}
