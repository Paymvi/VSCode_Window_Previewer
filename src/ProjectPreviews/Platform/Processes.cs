using System.Runtime.InteropServices;

namespace ProjectPreviews.Platform;

/// <summary>Process identity = PID + start time (as AI Status Indicator records it), so a reused PID never matches.</summary>
public static class Processes
{
    private const uint STILL_ACTIVE = 259, TH32CS_SNAPPROCESS = 2;
    private const double StartToleranceMs = 50;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(nint h, out uint code);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint h, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(nint snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(nint snap, ref PROCESSENTRY32W e);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    /// <summary>1 = running (start time returned), 0 = no such running process, -1 = can't tell.</summary>
    public static int StartTime(int pid, out DateTime startedUtc)
    {
        startedUtc = DateTime.MinValue;
        if (pid <= 0) return 0;
        nint h = OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return Marshal.GetLastWin32Error() == 87 ? 0 : -1; // 87: no process with that ID
        try
        {
            if (GetExitCodeProcess(h, out uint code) && code != STILL_ACTIVE) return 0;
            if (!GetProcessTimes(h, out long c, out _, out _, out _)) return -1;
            startedUtc = DateTime.FromFileTimeUtc(c);
            return 1;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>1 = the recorded process is running, 0 = gone (or the PID now belongs to another process), -1 = unknown.</summary>
    public static int Alive(int pid, DateTime startedUtc)
    {
        if (pid <= 0 || startedUtc == DateTime.MinValue) return -1;
        int r = StartTime(pid, out var actual);
        if (r != 1) return r;
        return Math.Abs((actual - startedUtc).TotalMilliseconds) <= StartToleranceMs ? 1 : 0;
    }

    public static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        nint snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == 0 || snap == -1) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
        }
        finally { CloseHandle(snap); }
        return map;
    }

    /// <summary>
    /// Parents of <paramref name="pid"/>, nearest first. A parent must have started before its child; otherwise the
    /// recorded parent exited and its PID was reused, and the chain stops there.
    /// </summary>
    public static List<int> Ancestors(int pid, DateTime startedUtc, IReadOnlyDictionary<int, int> parents, int max = 8)
    {
        var chain = new List<int>();
        DateTime childStart = startedUtc;
        for (int cur = pid; chain.Count < max && parents.TryGetValue(cur, out int parent) && parent > 4 && !chain.Contains(parent); cur = parent)
        {
            if (StartTime(parent, out var ps) != 1 || ps > childStart.AddMilliseconds(StartToleranceMs)) break;
            chain.Add(parent);
            childStart = ps;
        }
        return chain;
    }
}
