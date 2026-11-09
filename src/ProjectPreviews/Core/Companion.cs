using System.Text.Json;

namespace ProjectPreviews.Core;

/// <summary>What the companion extension reports for one VS Code window (one extension host).</summary>
public sealed record CompanionReport(
    string SessionId, int Pid, string Name, string? WorkspaceFile, IReadOnlyList<string> Folders,
    string? ActiveFile, bool Focused, long FocusedAt, long UpdatedAt)
{
    public static CompanionReport? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string? Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long Num(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : 0;
            var folders = r.TryGetProperty("folders", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => s.Length > 0).ToList()
                : [];
            string? session = Str("sessionId");
            int pid = (int)Num("pid");
            if (string.IsNullOrEmpty(session) || pid <= 0) return null;
            return new CompanionReport(session, pid, Str("name") ?? "", Str("workspaceFile"), folders,
                Str("activeFile"), r.TryGetProperty("focused", out var fo) && fo.ValueKind == JsonValueKind.True,
                Num("focusedAt"), Num("updatedAt"));
        }
        catch (JsonException) { return null; }
    }

    public ProjectIdentity ToIdentity()
    {
        // A saved .code-workspace file: identity is the workspace file.
        if (WorkspaceFile is { Length: > 0 } wf && wf != "untitled")
            return new(ProjectKind.Workspace, Aliases.NormalizeKey(wf), wf, StripWorkspace(Name), true);
        // One folder (including an untitled workspace made by "Duplicate Workspace"): that folder is the project.
        if (Folders.Count == 1)
            return new(ProjectKind.Folder, Aliases.NormalizeKey(Folders[0]), Folders[0], LastSegment(Folders[0]), true);
        if (Folders.Count > 1)
            return new(ProjectKind.Workspace, null, null, StripWorkspace(Name) is { Length: > 0 } n ? n : "Untitled workspace", true);
        return ProjectIdentity.Empty;
    }

    internal static string StripWorkspace(string name) =>
        name.EndsWith("(Workspace)", StringComparison.Ordinal) ? name[..^"(Workspace)".Length].Trim() : name.Trim();

    internal static string LastSegment(string pathOrUri)
    {
        string s = pathOrUri.TrimEnd('/', '\\');
        int i = s.LastIndexOfAny(['/', '\\']);
        return Uri.UnescapeDataString(i >= 0 ? s[(i + 1)..] : s);
    }
}

public readonly record struct FocusEvent(nint Hwnd, long AtMs);

public sealed record WindowTitle(nint Hwnd, string Title);

/// <summary>
/// Pairs VS Code windows (HWNDs) with companion reports. The extension can't see its own HWND, so:
/// 1) when a window becomes foreground and a report says "I got focus" at the same moment, they're the same window (sticky);
/// 2) otherwise the title must contain the report's workspace name (and ideally its active file).
/// Windows nothing matches stay unbound and fall back to title-only identity.
/// </summary>
public static class Binder
{
    public const long FocusWindowMs = 1500;

    public static Dictionary<nint, CompanionReport> Bind(
        IReadOnlyList<WindowTitle> windows, IReadOnlyList<CompanionReport> reports,
        IReadOnlyList<FocusEvent> focusEvents, Dictionary<nint, string> sticky, nint foreground)
    {
        var live = windows.Select(w => w.Hwnd).ToHashSet();
        var bySession = reports.ToDictionary(r => r.SessionId);
        foreach (var h in sticky.Keys.ToList())
            if (!live.Contains(h) || !bySession.ContainsKey(sticky[h])) sticky.Remove(h);

        void Stick(nint hwnd, string session)
        {
            foreach (var (h, s) in sticky.ToList())
                if (s == session || h == hwnd) sticky.Remove(h);
            sticky[hwnd] = session;
        }

        // Oldest first, so the most recent focus correlation is the one that sticks.
        foreach (var r in reports.Where(r => r.FocusedAt > 0).OrderBy(r => r.FocusedAt))
        {
            var best = focusEvents.Where(e => live.Contains(e.Hwnd) && Math.Abs(e.AtMs - r.FocusedAt) <= FocusWindowMs)
                .OrderBy(e => Math.Abs(e.AtMs - r.FocusedAt)).Select(e => (nint?)e.Hwnd).FirstOrDefault();
            if (best is { } h) Stick(h, r.SessionId);
        }
        var focused = reports.Where(r => r.Focused).ToList();
        if (focused.Count == 1 && live.Contains(foreground)) Stick(foreground, focused[0].SessionId);

        // A binding the title contradicts is stale (e.g. an empty window just opened a folder: for a few seconds the
        // old and new extension hosts both report). Only checked for VS Code's default title format.
        var titles = windows.ToDictionary(w => w.Hwnd, w => w.Title);
        foreach (var (h, s) in sticky.ToList())
            if (HasProductName(titles[h]) && TitleScore(titles[h], bySession[s]) == 0) sticky.Remove(h);

        var result = sticky.ToDictionary(kv => kv.Key, kv => bySession[kv.Value]);
        var freeReports = reports.Where(r => !result.Values.Contains(r)).ToList();
        var freeWindows = windows.Where(w => !result.ContainsKey(w.Hwnd)).ToList();

        var pairs = new List<(int Score, WindowTitle W, CompanionReport R)>();
        foreach (var w in freeWindows)
            foreach (var r in freeReports)
                if (TitleScore(w.Title, r) is var s and > 0) pairs.Add((s, w, r));
        foreach (var (_, w, r) in pairs.OrderByDescending(p => p.Score))
        {
            if (result.ContainsKey(w.Hwnd) || result.Values.Contains(r)) continue;
            result[w.Hwnd] = r;
        }
        return result;
    }

    private static bool HasProductName(string title) =>
        title.Split(" - ").Any(p => p.Trim().StartsWith("Visual Studio Code", StringComparison.Ordinal));

    /// <summary>0 = can't be this window. Requires the workspace name as a whole title part; the active file adds confidence.</summary>
    internal static int TitleScore(string title, CompanionReport r)
    {
        var parts = title.Split(" - ").Select(p => p.Trim().TrimStart('●', '•', ' ')).ToList();
        int app = parts.FindLastIndex(p => p.StartsWith("Visual Studio Code", StringComparison.Ordinal));
        if (app < 0) return 0;
        parts = parts.Take(app).ToList();
        bool fileMatch = r.ActiveFile is { Length: > 0 } f && parts.Count > 0 &&
            (parts[0].StartsWith(f, StringComparison.Ordinal) || f.StartsWith(parts[0], StringComparison.Ordinal));
        if (r.Name.Length == 0) // empty window: title has at most the file
            return parts.Count == 0 && r.ActiveFile is null ? 2 : parts.Count == 1 && fileMatch ? 2 : 0;
        if (!parts.Contains(r.Name, StringComparer.Ordinal)) return 0;
        return fileMatch || (r.ActiveFile is null && parts.Count >= 1 && parts[0] == r.Name) ? 3 : 2;
    }
}
