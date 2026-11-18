using System.Text.Json;

namespace ProjectPreviews.Core;

public enum WorkerProvider { Claude, Codex }

/// <summary>Colour/glyph family of a worker's status. Always shown together with text.</summary>
public enum WorkerTone { Working, Attention, Done, Error, Stale }

/// <summary>
/// One Claude Code or Codex session, as AI Status Indicator's hook (hook.ps1) records it in
/// state\sessions\&lt;id&gt;.json. Only identity, matching and status fields are read; the files never hold prompts.
/// </summary>
public sealed record WorkerSession(
    string Id, WorkerProvider Provider, string State, string Event, string Detail,
    DateTime Updated, DateTime StateSince, DateTime FirstSeen, int Pid, DateTime PidStarted,
    bool InVsCode, string VsWindow)
{
    public const string Working = "WORKING", NeedsYou = "NEEDS_YOU", Done = "DONE", Error = "ERROR";

    /// <summary>Null for malformed files and states AI Status Indicator doesn't define.</summary>
    public static WorkerSession? Parse(string id, string json, DateTime fileWrittenUtc)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            DateTime Time(string n, DateTime fallback) =>
                DateTime.TryParse(Str(n), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : fallback;
            string state = Str("state");
            if (state is not (Working or NeedsYou or Done or Error)) return null;
            var updated = Time("updated", fileWrittenUtc);
            var since = Time("state_since", updated);
            int pid = r.TryGetProperty("claude_pid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var x) && x > 0 ? x : 0;
            var started = Time("claude_started", DateTime.MinValue);
            if (started == DateTime.MinValue) pid = 0; // a PID without its start time could be a reused one
            return new WorkerSession(id, Str("provider") == "codex" ? WorkerProvider.Codex : WorkerProvider.Claude, state, Str("event"), Str("detail"),
                updated, since, Time("first_seen", since), pid, started,
                r.TryGetProperty("vscode", out var vs) && vs.ValueKind == JsonValueKind.True, Str("vscode_window"));
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>A worker that passed the liveness checks, with what the matcher needs.</summary>
/// <param name="Ancestors">Verified parent chain of the worker's process (nearest first).</param>
/// <param name="LastActivity">Latest sign of life: the file's update or the hook's working heartbeat.</param>
public sealed record LiveWorker(WorkerSession Session, IReadOnlyList<int> Ancestors, DateTime LastActivity);

/// <summary>A VS Code window's extension host, from the companion report bound to that card (Hwnd 0 = report not bound to a card).</summary>
public sealed record WindowHost(nint Hwnd, int HostPid, string Session);

public sealed record WorkerChip(string Label, string Status, WorkerTone Tone, string Tooltip, WorkerProvider Provider, int Look, string State);

public static class Workers
{
    /// <summary>Same as AI Status Indicator's default staleHours: an active state with no update this long is gone.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);
    /// <summary>WORKING with no sign of life for this long is shown as "No recent activity" (same as the Calendar's Codex quiet time).</summary>
    public static readonly TimeSpan QuietAfter = TimeSpan.FromMinutes(20);

    /// <param name="alive">1 = the recorded process is running, 0 = it's gone, -1 = can't tell.</param>
    public static bool Keep(WorkerSession w, int alive, DateTime nowUtc)
    {
        if (alive == 0) return false;
        if (w.Pid == 0) return nowUtc - w.Updated < StaleAfter; // no process identity: only fresh data
        return w.State == WorkerSession.Done || nowUtc - w.Updated < StaleAfter;
    }

    /// <summary>
    /// One process, one worker: a Claude Code process runs one conversation at a time, so older sessions it left behind
    /// (e.g. after /clear) are dropped. A Codex app-server runs several threads at once, so its active threads all stay;
    /// a finished one stays only while it's that process's most recent.
    /// </summary>
    public static List<LiveWorker> Dedupe(IEnumerable<LiveWorker> workers)
    {
        var result = new List<LiveWorker>();
        foreach (var g in workers.GroupBy(w => w.Session.Pid > 0 ? (w.Session.Pid, w.Session.PidStarted) : (0, DateTime.MinValue)))
        {
            if (g.Key.Item1 == 0) { result.AddRange(g); continue; }
            var latest = g.OrderByDescending(w => w.Session.Updated).ThenBy(w => w.Session.Id, StringComparer.Ordinal).First();
            result.AddRange(g.Where(w => w == latest || (w.Session.Provider == WorkerProvider.Codex && w.Session.State != WorkerSession.Done)));
        }
        return result;
    }

    /// <summary>
    /// Assigns workers to cards. Only exact identifiers count: the worker's process descends from a window's extension
    /// host (extension-hosted agents), or it recorded that window's id (agents in that window's terminals). Paths and
    /// titles are never used. A worker known to run in VS Code that can't be placed is counted, never guessed.
    /// </summary>
    public static (Dictionary<nint, List<LiveWorker>> ByWindow, int Unmatched) Match(IEnumerable<LiveWorker> workers, IReadOnlyList<WindowHost> hosts)
    {
        var byPid = new Dictionary<int, WindowHost>();
        foreach (var h in hosts) byPid.TryAdd(h.HostPid, h);
        var bySession = hosts.Where(h => h.Session.Length > 0).GroupBy(h => h.Session).ToDictionary(g => g.Key, g => g.First());
        var result = new Dictionary<nint, List<LiveWorker>>();
        int unmatched = 0;
        foreach (var w in workers)
        {
            WindowHost? host = w.Ancestors.Select(a => byPid.GetValueOrDefault(a)).FirstOrDefault(h => h is not null);
            if (host is null && w.Session.VsWindow.Length > 0) host = bySession.GetValueOrDefault(w.Session.VsWindow);
            if (host is { Hwnd: not 0 })
            {
                if (!result.TryGetValue(host.Hwnd, out var list)) result[host.Hwnd] = list = [];
                list.Add(w);
            }
            else if (host is not null || w.Session.InVsCode || w.Session.VsWindow.Length > 0) unmatched++;
        }
        return (result, unmatched);
    }

    /// <summary>Chips for one card: Claude first, then Codex, each in the order the sessions appeared; "#n" only when a provider repeats.</summary>
    public static List<WorkerChip> Chips(IEnumerable<LiveWorker> workers, DateTime nowUtc)
    {
        var ordered = workers.OrderBy(w => w.Session.Provider).ThenBy(w => w.Session.FirstSeen).ThenBy(w => w.Session.Id, StringComparer.Ordinal).ToList();
        var counts = ordered.GroupBy(w => w.Session.Provider).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<WorkerProvider, int>();
        var usedHats = new HashSet<int>();
        var chips = new List<WorkerChip>();
        foreach (var w in ordered)
        {
            var s = w.Session;
            int n = seen[s.Provider] = seen.GetValueOrDefault(s.Provider) + 1;
            string name = s.Provider == WorkerProvider.Codex ? "Codex" : "Claude";
            string label = counts[s.Provider] > 1 ? $"{name} #{n}" : name;
            var (status, full, tone) = Describe(w, nowUtc);
            string product = s.Provider == WorkerProvider.Codex ? "Codex" : "Claude Code";
            string tip = $"{product}{(counts[s.Provider] > 1 ? $" #{n}" : "")} · session {Short(s.Id)}\n{full}";
            chips.Add(new WorkerChip(label, status, tone, tip, s.Provider, Look(s, usedHats), s.State));
        }
        return chips;
    }

    /// <summary>Short chip text, full tooltip text and tone. Only distinctions the hook actually records.</summary>
    public static (string Short, string Full, WorkerTone Tone) Describe(LiveWorker w, DateTime nowUtc)
    {
        var s = w.Session;
        var t = nowUtc - s.StateSince;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        switch (s.State)
        {
            case WorkerSession.Working:
                var quiet = nowUtc - w.LastActivity;
                if (quiet > QuietAfter)
                    return ("Quiet", $"Last reported working {Span(quiet)} ago. Nothing since, so it may have been interrupted (interruptions aren't always reported).", WorkerTone.Stale);
                return ("Working", $"Working · {Clock(t)}", WorkerTone.Working);
            case WorkerSession.NeedsYou:
                bool approval = s.Event == "PermissionRequest" || s.Detail == "permission_prompt";
                bool input = s.Detail is "elicitation_dialog" or "elicitation_url_dialog" or "agent_needs_input" or "AskUserQuestion";
                string what = approval ? "Needs approval" : input ? "Needs input" : "Needs you";
                string tool = approval && s.Detail.Length > 0 && s.Detail != "permission_prompt" ? $" ({s.Detail})" : "";
                // Chips are narrow: the short word keeps the distinction, the tooltip says it in full.
                return (approval ? "Approval" : input ? "Input" : what, $"{what}{tool} · waiting {Span(t)}", WorkerTone.Attention);
            case WorkerSession.Done:
                return ("Done", $"Done · finished {Ago(t)}", WorkerTone.Done);
            default:
                string e = s.Detail.Length > 0 ? $" ({s.Detail})" : "";
                return ("Error", $"Error{e} · {Ago(t)}", WorkerTone.Error);
        }
    }

    /// <summary>
    /// A creature look from AI Status Indicator's sheet (13 hats × 8 bodies, phone.js). Claude gets the orange family
    /// (bodies 0-3), Codex lavender / slate blue (4-5); stable per session, and distinct hats within a card where possible.
    /// </summary>
    internal static int Look(WorkerSession s, HashSet<int> usedHats)
    {
        uint h = 2166136261;
        foreach (char c in s.Id) h = (h ^ c) * 16777619;
        int hat = (int)(h % 13);
        for (int i = 0; i < 13 && usedHats.Contains(hat); i++) hat = (hat + 1) % 13;
        usedHats.Add(hat);
        int body = s.Provider == WorkerProvider.Codex ? 4 + (int)(h / 13 % 2) : (int)(h / 13 % 4);
        return body * 13 + hat;
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    // Timer formats as on AI Status Indicator's board (Board.Clock / Span / Ago).
    internal static string Clock(TimeSpan t) =>
        t.TotalHours < 1 ? $"{(int)t.TotalMinutes:00}:{t.Seconds:00}" : t.TotalDays < 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{(int)t.TotalDays}d {t.Hours}h";
    internal static string Span(TimeSpan t) =>
        t.TotalMinutes < 1 ? "<1m" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m" : t.TotalDays < 1 ? $"{(int)t.TotalHours}h{(t.Minutes > 0 ? $" {t.Minutes}m" : "")}" : $"{(int)t.TotalDays}d";
    internal static string Ago(TimeSpan t) =>
        t.TotalMinutes < 1 ? "just now" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m ago" : t.TotalDays < 1 ? $"{(int)t.TotalHours}h ago" : $"{(int)t.TotalDays}d ago";
}
