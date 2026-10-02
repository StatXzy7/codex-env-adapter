using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexEnvAdapter;

sealed class ProcessScope
{
    readonly int _self = Environment.ProcessId;
    readonly int _watchPid;
    readonly int _onlyPid;
    readonly string _install;
    readonly ConcurrentDictionary<int, string> _images = new();
    readonly ConcurrentDictionary<int, (bool target, long tick)> _cache = new();

    public ProcessScope(string? installLocation, int watchPid, int onlyPid)
    {
        _install = installLocation?.TrimEnd('\\') ?? "";
        _watchPid = watchPid;
        _onlyPid = onlyPid;
    }

    public bool IsTarget(int pid)
    {
        if (pid <= 0 || pid == _self)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (_cache.TryGetValue(pid, out var hit) && now - hit.tick < 1000)
        {
            return hit.target;
        }

        var value = Matches(pid, ProcessSnapshot.Capture());
        _cache[pid] = (value, now);
        return value;
    }

    bool Matches(int pid, ProcessSnapshot snap)
    {
        var current = pid;
        for (var depth = 0; depth < 8 && current > 0; depth++)
        {
            if (_onlyPid != 0 && current == _onlyPid)
            {
                return true;
            }

            if (depth > 0 && _watchPid != 0 && current == _watchPid)
            {
                return true;
            }

            if (current != _self
                && snap.Names.TryGetValue(current, out var name)
                && name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current != _self && ImageMatches(current))
            {
                return true;
            }

            if (!snap.Parents.TryGetValue(current, out var parent) || parent == 0 || parent == current)
            {
                break;
            }

            current = parent;
        }

        return false;
    }

    bool ImageMatches(int pid)
    {
        var path = Image(pid);
        if (path.Length == 0)
        {
            return false;
        }

        if (_install.Length > 0 && path.StartsWith(_install, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.Contains("\\OpenAI.Codex", StringComparison.OrdinalIgnoreCase);
    }

    string Image(int pid)
    {
        if (_images.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        var path = QueryImage(pid) ?? "";
        _images[pid] = path;
        return path;
    }

    static string? QueryImage(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr handle, int flags, StringBuilder name, ref int size);
}

sealed class ProcessSnapshot
{
    public Dictionary<int, int> Parents { get; } = new();
    public Dictionary<int, string> Names { get; } = new();

    public static ProcessSnapshot Capture()
    {
        var snap = new ProcessSnapshot();
        var handle = CreateToolhelp32Snapshot(0x00000002, 0);
        if (handle == new IntPtr(-1))
        {
            return snap;
        }

        try
        {
            var entry = new ProcessEntry { dwSize = Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(handle, ref entry))
            {
                return snap;
            }

            do
            {
                if (entry.th32ProcessID <= 0)
                {
                    continue;
                }

                snap.Parents[entry.th32ProcessID] = entry.th32ParentProcessID;
                snap.Names[entry.th32ProcessID] = entry.szExeFile ?? "";
            }
            while (Process32Next(handle, ref entry));
        }
        finally
        {
            CloseHandle(handle);
        }

        return snap;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public int dwSize;
        public int cntUsage;
        public int th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public int th32ModuleID;
        public int cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}

static class SocketOwners
{
    public static Dictionary<ushort, int> Tcp() => Read(false);
    public static Dictionary<ushort, int> Udp() => Read(true);

    static Dictionary<ushort, int> Read(bool udp)
    {
        var map = new Dictionary<ushort, int>();
        var size = 0;
        var kind = udp ? 1 : 5;
        var probe = udp ? GetExtendedUdpTable(IntPtr.Zero, ref size, false, 2, kind, 0) : GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, kind, 0);
        if (size <= 0)
        {
            return map;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = udp
                ? GetExtendedUdpTable(buffer, ref size, false, 2, kind, 0)
                : GetExtendedTcpTable(buffer, ref size, false, 2, kind, 0);
            if (result != 0)
            {
                return map;
            }

            var count = Marshal.ReadInt32(buffer);
            var row = udp ? 12 : 24;
            for (var i = 0; i < count && i < 200000; i++)
            {
                var ptr = buffer + 4 + i * row;
                if (!udp)
                {
                    var state = (uint)Marshal.ReadInt32(ptr);
                    if (state is 1 or 2 or 11)
                    {
                        continue;
                    }
                }

                var portRaw = Marshal.ReadInt32(ptr, udp ? 4 : 8);
                var pid = Marshal.ReadInt32(ptr, udp ? 8 : 20);
                if (pid <= 0)
                {
                    continue;
                }

                map[HostPort(portRaw)] = pid;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return map;
    }

    public static ushort HostPort(int raw)
    {
        var value = (uint)raw;
        return (ushort)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
