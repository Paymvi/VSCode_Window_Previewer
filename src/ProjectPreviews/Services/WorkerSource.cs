using System.Windows.Threading;
using ProjectPreviews.Core;
using ProjectPreviews.Platform;

namespace ProjectPreviews.Services;

/// <param name="Available">False: AI Status Indicator's status folder doesn't exist or can't be read (distinct from "no workers").</param>
public sealed record WorkerSnapshot(bool Available, IReadOnlyList<LiveWorker> Workers)
{
    public static WorkerSnapshot None { get; } = new(false, []);
}

/// <summary>
/// Reads AI Status Indicator's per-session status files (state\sessions\*.json). Its hooks write them from the Claude
/// Code / Codex processes themselves, so they stay current whether or not the indicator window runs; this class only
/// reads them (never writes or deletes) and starts no collector of its own. A FileSystemWatcher reports changes; a
/// light poll (liveness, staleness) runs only while the picker is open. All file and process work is off the UI thread.
/// </summary>
public sealed class WorkerSource : IDisposable
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } c ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "status-indicator");

    private readonly string _root;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private FileSystemWatcher? _watcher;
    private bool _running, _loading, _again, _fast;

    public WorkerSource(string root)
    {
        _root = root;
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        _poll.Tick += (_, _) => { if (_watcher is null) TryWatch(); Refresh(); };
    }

    public string Root => _root;
    public string SessionsDir => Path.Combine(_root, "state", "sessions");
    public WorkerSnapshot Current { get; private set; } = WorkerSnapshot.None;
    public bool Running => _running;
    /// <summary>A load has completed since Start (so "unavailable" is a finding, not "not looked yet").</summary>
    public bool HasData { get; private set; }
    /// <summary>Raised on the UI thread after each completed load.</summary>
    public event Action? Changed;

    public void Start()
    {
        if (_running) return;
        _running = true;
        TryWatch();
        UpdatePoll();
        Refresh();
    }

    /// <summary>Stops all monitoring (watcher, timers) and forgets the data. Nothing else is affected.</summary>
    public void Stop()
    {
        _running = false;
        _watcher?.Dispose();
        _watcher = null;
        _debounce.Stop();
        _poll.Stop();
        Current = WorkerSnapshot.None;
        HasData = false;
    }

    /// <summary>On while the picker is open: re-checks every few seconds (process exits produce no file event).</summary>
    public void SetFast(bool on)
    {
        _fast = on;
        UpdatePoll();
        if (on) Refresh();
    }

    // Without a watcher (folder missing) a slow retry notices when AI Status Indicator gets installed.
    private void UpdatePoll()
    {
        _poll.Interval = TimeSpan.FromSeconds(_fast ? 3 : 30);
        if (_running && (_fast || _watcher is null)) _poll.Start(); else _poll.Stop();
    }

    private void TryWatch()
    {
        if (_watcher is not null || !_running) return;
        try
        {
            if (!Directory.Exists(SessionsDir)) return;
            var w = new FileSystemWatcher(SessionsDir, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, IncludeSubdirectories = false };
            FileSystemEventHandler kick = (_, _) => _ui.BeginInvoke(() => { if (_running) { _debounce.Stop(); _debounce.Start(); } });
            w.Created += kick; w.Changed += kick; w.Deleted += kick;
            w.Renamed += (_, _) => kick(null!, null!);
            w.Error += (_, _) => _ui.BeginInvoke(() => { _watcher?.Dispose(); _watcher = null; UpdatePoll(); }); // e.g. folder deleted: retry later
            w.EnableRaisingEvents = true;
            _watcher = w;
            UpdatePoll();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public void Refresh()
    {
        if (!_running) return;
        if (_loading) { _again = true; return; }
        _loading = true;
        var prev = Current;
        Task.Run(() => Load(_root, DateTime.UtcNow, prev)).ContinueWith(t => _ui.BeginInvoke(() =>
        {
            _loading = false;
            if (!_running) return;
            Current = t.IsCompletedSuccessfully ? t.Result : WorkerSnapshot.None;
            HasData = true;
            if (t.Exception is { } ex) Log.Write($"worker status load failed: {ex.GetBaseException().Message}");
            if (_watcher is null) TryWatch();
            Changed?.Invoke();
            if (_again) { _again = false; Refresh(); }
        }));
    }

    public void Dispose() => Stop();

    /// <summary>One pass over the status files. A file that's mid-replace keeps its previous reading.</summary>
    internal static WorkerSnapshot Load(string root, DateTime nowUtc, WorkerSnapshot prev)
    {
        string dir = Path.Combine(root, "state", "sessions"), ticks = Path.Combine(root, "state", "activity", "tick");
        string[] files;
        try { files = Directory.GetFiles(dir, "*.json"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return WorkerSnapshot.None; }
        var before = prev.Workers.ToDictionary(w => w.Session.Id, StringComparer.OrdinalIgnoreCase);
        Dictionary<int, int>? parents = null;
        var list = new List<LiveWorker>();
        foreach (var f in files)
        {
            string id = Path.GetFileNameWithoutExtension(f);
            WorkerSession? s;
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > 64 * 1024) continue; // not one of ours
                using var reader = new StreamReader(fs);
                s = WorkerSession.Parse(id, reader.ReadToEnd(), File.GetLastWriteTimeUtc(f));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (before.TryGetValue(id, out var keep)) list.Add(keep);
                continue;
            }
            if (s is null || !Workers.Keep(s, Processes.Alive(s.Pid, s.PidStarted), nowUtc)) continue;
            var ancestors = s.Pid > 0 ? Processes.Ancestors(s.Pid, s.PidStarted, parents ??= Processes.ParentMap()) : [];
            var last = s.Updated;
            try { var tick = File.GetLastWriteTimeUtc(Path.Combine(ticks, id)); if (tick.Year > 2000 && tick > last) last = tick; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            list.Add(new LiveWorker(s, ancestors, last));
        }
        return new WorkerSnapshot(true, Workers.Dedupe(list));
    }
}
