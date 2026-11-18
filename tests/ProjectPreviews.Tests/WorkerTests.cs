using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using ProjectPreviews.Core;
using ProjectPreviews.Platform;
using ProjectPreviews.Services;

namespace ProjectPreviews.Tests;

public class WorkerSettingsTests
{
    [Fact]
    public void Existing_settings_file_without_the_new_fields_defaults_to_off_and_creatures()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pp-ws-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "Version": 1, "Enabled": true, "CardScale": 1.2 }""");
        try
        {
            var s = SettingsStore.Load(path, out var problem);
            Assert.Null(problem);
            Assert.False(s.ShowWorkers);
            Assert.Equal(WorkerStyle.Creatures, s.WorkerStyle);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Toggle_and_style_persist_across_save_and_load()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pp-ws-{Guid.NewGuid():N}.json");
        try
        {
            SettingsStore.Save(path, new AppSettings { ShowWorkers = true, WorkerStyle = WorkerStyle.Indicators });
            Assert.Contains("\"Indicators\"", File.ReadAllText(path)); // readable, as with Theme
            var s = SettingsStore.Load(path, out _);
            Assert.True(s.ShowWorkers);
            Assert.Equal(WorkerStyle.Indicators, s.WorkerStyle);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Unknown_style_number_falls_back_to_creatures()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pp-ws-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "ShowWorkers": true, "WorkerStyle": 7 }""");
        try { Assert.Equal(WorkerStyle.Creatures, SettingsStore.Load(path, out _).WorkerStyle); }
        finally { File.Delete(path); }
    }
}

public class WorkerLogicTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 20, 0, 0, DateTimeKind.Utc);

    private static WorkerSession S(string id, WorkerProvider p = WorkerProvider.Claude, string state = WorkerSession.Working, int pid = 100,
        double minAgo = 1, string ev = "", string detail = "", bool vs = true, string win = "", double firstMin = 10) =>
        new(id, p, state, ev, detail, Now.AddMinutes(-minAgo), Now.AddMinutes(-minAgo), Now.AddMinutes(-firstMin), pid,
            pid > 0 ? Now.AddHours(-1) : DateTime.MinValue, vs, win);

    private static LiveWorker L(WorkerSession s, params int[] ancestors) => new(s, ancestors, s.Updated);

    [Fact]
    public void Parses_hook_file_and_rejects_malformed_or_unknown_states()
    {
        string json = """{"v":2,"session_id":"a1","state":"NEEDS_YOU","event":"PermissionRequest","detail":"Bash","project":"x","updated":"2026-11-17T19:59:00Z","state_since":"2026-11-17T19:58:00Z","first_seen":"2026-11-17T19:00:00Z","claude_pid":42,"claude_started":"2026-11-17T18:00:00Z","provider":"codex","vscode":true,"vscode_window":"win-1"}""";
        var s = WorkerSession.Parse("a1", json, Now)!;
        Assert.Equal(WorkerProvider.Codex, s.Provider);
        Assert.Equal((42, "win-1", true), (s.Pid, s.VsWindow, s.InVsCode));
        Assert.Equal(new DateTime(2026, 11, 17, 19, 58, 0, DateTimeKind.Utc), s.StateSince);

        Assert.Null(WorkerSession.Parse("x", "{not json", Now));
        Assert.Null(WorkerSession.Parse("x", "[]", Now));
        Assert.Null(WorkerSession.Parse("x", """{"state":"THINKING"}""", Now));
        // v2 Claude file from before this change: Claude, no window id, PID without start time is not trusted
        var old = WorkerSession.Parse("o", """{"state":"WORKING","claude_pid":42,"claude_started":""}""", Now)!;
        Assert.Equal((WorkerProvider.Claude, 0, ""), (old.Provider, old.Pid, old.VsWindow));
    }

    [Fact]
    public void Claude_and_Codex_in_one_card_are_both_shown_and_labeled()
    {
        var chips = Workers.Chips([L(S("c1")), L(S("x1", WorkerProvider.Codex, WorkerSession.Done))], Now);
        Assert.Equal(["Claude", "Codex"], chips.Select(c => c.Label));
        Assert.Equal(["Working", "Done"], chips.Select(c => c.Status));
        Assert.Contains("Claude Code", chips[0].Tooltip);
        Assert.Contains("Codex", chips[1].Tooltip);
        // Claude: orange bodies (0-3); Codex: lavender / slate blue (4-5)
        Assert.InRange(chips[0].Look / 13, 0, 3);
        Assert.InRange(chips[1].Look / 13, 4, 5);
    }

    [Fact]
    public void Several_workers_of_one_provider_are_numbered_by_first_seen_with_distinct_hats()
    {
        var chips = Workers.Chips([L(S("late", firstMin: 1)), L(S("early", firstMin: 30)), L(S("mid", firstMin: 5))], Now);
        Assert.Equal(["Claude #1", "Claude #2", "Claude #3"], chips.Select(c => c.Label));
        Assert.Contains("session early", chips[0].Tooltip);
        Assert.Equal(3, chips.Select(c => c.Look % 13).Distinct().Count());
    }

    [Fact]
    public void Statuses_use_only_what_the_hook_records()
    {
        string St(WorkerSession s) => Workers.Describe(L(s), Now).Short;
        Assert.Equal("Approval", St(S("a", state: WorkerSession.NeedsYou, ev: "PermissionRequest", detail: "Bash")));
        Assert.Equal("Approval", St(S("a", state: WorkerSession.NeedsYou, ev: "Notification", detail: "permission_prompt")));
        Assert.Equal("Input", St(S("a", state: WorkerSession.NeedsYou, ev: "PreToolUse", detail: "AskUserQuestion")));
        Assert.Equal("Input", St(S("a", state: WorkerSession.NeedsYou, ev: "Notification", detail: "elicitation_dialog")));
        Assert.Equal("Needs you", St(S("a", state: WorkerSession.NeedsYou, ev: "Notification", detail: "something_new")));
        Assert.Equal("Error", St(S("a", state: WorkerSession.Error, detail: "rate_limit")));
        Assert.Contains("rate_limit", Workers.Describe(L(S("a", state: WorkerSession.Error, detail: "rate_limit")), Now).Full);
        Assert.Contains("Bash", Workers.Describe(L(S("a", state: WorkerSession.NeedsYou, ev: "PermissionRequest", detail: "Bash")), Now).Full);
    }

    [Fact]
    public void Old_working_data_is_not_shown_as_current_working()
    {
        var quiet = new LiveWorker(S("q", minAgo: 45), [], Now.AddMinutes(-45));
        Assert.Equal(WorkerTone.Stale, Workers.Describe(quiet, Now).Tone);
        Assert.Equal("Quiet", Workers.Describe(quiet, Now).Short);
        Assert.Contains("Last reported working 45m ago", Workers.Describe(quiet, Now).Full);
        // a recent heartbeat (tick) keeps a long turn "Working"
        var busy = new LiveWorker(S("b", minAgo: 45), [], Now.AddMinutes(-1));
        Assert.Equal(WorkerTone.Working, Workers.Describe(busy, Now).Tone);
        // waiting for approval for a long time is still waiting, not stale
        Assert.Equal(WorkerTone.Attention, Workers.Describe(new LiveWorker(S("n", state: WorkerSession.NeedsYou, minAgo: 90), [], Now.AddMinutes(-90)), Now).Tone);
    }

    [Fact]
    public void Exited_and_stale_workers_are_dropped()
    {
        Assert.False(Workers.Keep(S("a"), alive: 0, Now));                                        // process exited
        Assert.True(Workers.Keep(S("a"), alive: 1, Now));
        Assert.True(Workers.Keep(S("a"), alive: -1, Now));                                        // can't tell: keep (as the indicator does)
        Assert.False(Workers.Keep(S("a", minAgo: 25 * 60), alive: 1, Now));                       // active state, no update for > 24 h
        Assert.True(Workers.Keep(S("a", state: WorkerSession.Done, minAgo: 25 * 60), alive: 1, Now)); // finished, process still open
        Assert.False(Workers.Keep(S("a", pid: 0, minAgo: 25 * 60), alive: -1, Now));               // no process identity: only fresh data
    }

    [Fact]
    public void Matches_by_process_ancestry_even_when_two_windows_show_the_same_workspace()
    {
        // Two windows with the same folder open: only the extension host PIDs tell them apart.
        var hosts = new List<WindowHost> { new(1, 500, "sess-a"), new(2, 600, "sess-b") };
        var (map, unmatched) = Workers.Match([L(S("w1", pid: 700), 500, 3), L(S("w2", WorkerProvider.Codex, pid: 800), 650, 600, 3)], hosts);
        Assert.Equal(0, unmatched);
        Assert.Equal("w1", Assert.Single(map[1]).Session.Id);
        Assert.Equal("w2", Assert.Single(map[2]).Session.Id);
    }

    [Fact]
    public void Matches_terminal_workers_by_window_id_and_never_by_anything_weaker()
    {
        var hosts = new List<WindowHost> { new(1, 500, "sess-a"), new(2, 600, "sess-b") };
        var terminal = L(S("t", pid: 900, win: "sess-b"), 880, 3);        // pwsh → pty host: no extension host in the chain
        var outside = L(S("o", pid: 901, vs: false), 3);                   // Windows Terminal: not in any VS Code window
        var unknown = L(S("u", pid: 902, vs: true), 3);                    // in VS Code, but no identifier
        var staleWin = L(S("s", pid: 903, win: "sess-closed"), 3);         // window it named is gone
        var (map, unmatched) = Workers.Match([terminal, outside, unknown, staleWin], hosts);
        Assert.Equal("t", Assert.Single(map[2]).Session.Id);
        Assert.False(map.ContainsKey(1));
        Assert.Equal(2, unmatched); // "u" and "s" are reported, never guessed; "o" is simply not a VS Code worker
    }

    [Fact]
    public void Worker_under_a_report_not_bound_to_a_card_is_counted_not_placed()
    {
        var (map, unmatched) = Workers.Match([L(S("w", pid: 700), 500)], [new WindowHost(0, 500, "sess-a")]);
        Assert.Empty(map);
        Assert.Equal(1, unmatched);
    }

    [Fact]
    public void One_process_one_worker_for_Claude_but_concurrent_Codex_threads_all_stay()
    {
        var oldClaude = L(S("old", state: WorkerSession.Done, minAgo: 30));
        var newClaude = L(S("new", minAgo: 1));
        var codexA = L(S("ca", WorkerProvider.Codex, pid: 200, minAgo: 2));
        var codexB = L(S("cb", WorkerProvider.Codex, WorkerSession.NeedsYou, pid: 200, minAgo: 3));
        var codexDone = L(S("cd", WorkerProvider.Codex, WorkerSession.Done, pid: 200, minAgo: 40));
        var ids = Workers.Dedupe([oldClaude, newClaude, codexA, codexB, codexDone]).Select(w => w.Session.Id).OrderBy(x => x);
        Assert.Equal(["ca", "cb", "new"], ids);
    }
}

/// <summary>Against real temporary folders and real processes; AI Status Indicator's window is never involved.</summary>
public class WorkerSourceTests
{
    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"pp-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "state", "sessions"));
        return root;
    }

    private static void Put(string root, string id, string state, int pid, DateTime started, string extra = "")
    {
        string now = DateTime.UtcNow.ToString("o");
        File.WriteAllText(Path.Combine(root, "state", "sessions", id + ".json"),
            $$"""{"v":2,"session_id":"{{id}}","state":"{{state}}","event":"x","detail":"","project":"p","updated":"{{now}}","state_since":"{{now}}","first_seen":"{{now}}","claude_pid":{{pid}},"claude_started":"{{started:o}}"{{extra}}}""");
    }

    [Fact]
    public void Missing_folder_is_unavailable_which_differs_from_no_workers()
    {
        var gone = WorkerSource.Load(Path.Combine(Path.GetTempPath(), $"pp-none-{Guid.NewGuid():N}"), DateTime.UtcNow, WorkerSnapshot.None);
        Assert.False(gone.Available);
        string root = NewRoot();
        try
        {
            var empty = WorkerSource.Load(root, DateTime.UtcNow, WorkerSnapshot.None);
            Assert.True(empty.Available);
            Assert.Empty(empty.Workers);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Live_process_is_kept_with_its_ancestry_and_exited_or_malformed_ones_are_not()
    {
        string root = NewRoot();
        try
        {
            using var me = Process.GetCurrentProcess();
            var started = me.StartTime.ToUniversalTime();
            Put(root, "alive", "WORKING", me.Id, started, ""","provider":"codex","vscode":true""");
            Put(root, "reused", "WORKING", me.Id, started.AddMinutes(-5));   // same PID, different start: another process
            Put(root, "gone", "DONE", 999999, started);
            File.WriteAllText(Path.Combine(root, "state", "sessions", "broken.json"), "{\"state\":\"WORK");
            Directory.CreateDirectory(Path.Combine(root, "state", "activity", "tick"));
            File.WriteAllText(Path.Combine(root, "state", "activity", "tick", "alive"), "");

            var snap = WorkerSource.Load(root, DateTime.UtcNow, WorkerSnapshot.None);
            var w = Assert.Single(snap.Workers);
            Assert.Equal(("alive", WorkerProvider.Codex), (w.Session.Id, w.Session.Provider));
            Assert.NotEmpty(w.Ancestors); // the test host's verified parent chain
            Assert.Equal(Processes.ParentMap()[me.Id], w.Ancestors[0]);
            Assert.True(w.LastActivity >= w.Session.Updated);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Picks_up_hook_writes_and_exits_while_running_with_the_indicator_window_closed()
    {
        // The real hook.ps1, run the way Claude Code runs it, writes into a temporary install; only the source watches.
        string? hook = FindHook();
        if (hook is null) return; // AI Status Indicator repo not next to this one: covered by its own tests
        string root = NewRoot();
        File.Copy(hook, Path.Combine(root, "hook.ps1"));
        WorkerSnapshot? seen = null;
        var t = new Thread(() =>
        {
            var source = new WorkerSource(root);
            var frame = new DispatcherFrame();
            int step = 0;
            source.Changed += () =>
            {
                var cur = source.Current;
                if (step == 0) { step = 1; RunHook(root, "working", "sess-live", Environment.ProcessId); }
                else if (step == 1 && cur.Workers.Any(w => w.Session.Id == "sess-live" && w.Session.State == WorkerSession.Working))
                { step = 2; RunHook(root, "end", "sess-live", Environment.ProcessId); }
                else if (step == 2 && cur.Workers.All(w => w.Session.Id != "sess-live")) { seen = cur; frame.Continue = false; }
            };
            source.Start();
            var timeout = new DispatcherTimer(TimeSpan.FromSeconds(40), DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame);
            source.Stop();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try
        {
            Assert.NotNull(seen); // saw it appear as WORKING from a file event, then disappear after SessionEnd
            Assert.True(seen!.Available);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static string? FindHook()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "AI-Status-Indicator", "src", "hook.ps1");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // Fire-and-forget on a worker thread, as a real hook process would run alongside us.
    private static void RunHook(string root, string action, string sid, int pid) => Task.Run(() =>
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "hook.ps1"), action])
        { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["CLAUDE_PID"] = pid.ToString();
        psi.Environment["PROJECT_PREVIEWS_WINDOW"] = "win-test";
        using var p = Process.Start(psi)!;
        p.StandardInput.Write($$"""{"session_id":"{{sid}}","hook_event_name":"{{(action == "end" ? "SessionEnd" : "UserPromptSubmit")}}","cwd":"C:\\x\\proj","prompt":"SECRET"}""");
        p.StandardInput.Close();
        p.WaitForExit(20000);
    });
}
