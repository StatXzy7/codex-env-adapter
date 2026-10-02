using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CodexEnvAdapter;

static class WinDivertNative
{
    public const int LayerNetwork = 0;
    public const int LayerSocket = 3;
    public const ulong FlagSniff = 0x0001;
    public const ulong FlagRecvOnly = 0x0004;
    public const int ParamQueueLength = 0;
    public const int ParamQueueTime = 1;
    public const int ParamQueueSize = 2;

    static readonly object Gate = new();
    static bool _loaded;

    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }

            if (!Environment.Is64BitProcess)
            {
                throw new InvalidOperationException("接管 Codex 直连只支持 64 位启动器。");
            }

            var dir = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = AppContext.BaseDirectory;
            }

            Materialize(dir);
            var dll = Path.Combine(dir, "WinDivert.dll");
            if (!File.Exists(dll))
            {
                Materialize(AppContext.BaseDirectory);
                dir = AppContext.BaseDirectory;
                dll = Path.Combine(dir, "WinDivert.dll");
            }

            if (!File.Exists(dll))
            {
                throw new FileNotFoundException("找不到 WinDivert.dll。请重新编译启动器。", dll);
            }

            var folder = Path.GetDirectoryName(dll)!;
            if (!File.Exists(Path.Combine(folder, "WinDivert64.sys")))
            {
                throw new FileNotFoundException("找不到 WinDivert64.sys。请把它和 WinDivert.dll 放在同一目录。");
            }

            SetDllDirectory(folder);
            if (LoadLibrary(dll) == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载 WinDivert.dll。");
            }

            _loaded = true;
        }
    }

    static void Materialize(string folder)
    {
        Directory.CreateDirectory(folder);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in new[] { "WinDivert.dll", "WinDivert64.sys" })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null)
            {
                continue;
            }

            using var file = File.Create(path);
            stream.CopyTo(file);
        }
    }

    public static IntPtr Open(string filter, int layer, short priority, ulong flags)
    {
        EnsureLoaded();
        return WinDivertOpen(filter, layer, priority, flags);
    }

    public static bool IsInvalid(IntPtr handle) => handle == IntPtr.Zero || handle == new IntPtr(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectory(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibrary(string path);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    static extern IntPtr WinDivertOpen(string filter, int layer, short priority, ulong flags);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertRecv(IntPtr handle, byte[] packet, uint packetLen, out uint recvLen, ref WinDivertAddress addr);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertRecv", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertRecvEvent(IntPtr handle, IntPtr packet, uint packetLen, out uint recvLen, ref WinDivertAddress addr);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertSend(IntPtr handle, byte[] packet, uint packetLen, out uint sendLen, ref WinDivertAddress addr);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertShutdown(IntPtr handle, int how);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertClose(IntPtr handle);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertSetParam(IntPtr handle, int param, ulong value);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertHelperCalcChecksums(byte[] packet, uint packetLen, ref WinDivertAddress addr, ulong flags);
}

[StructLayout(LayoutKind.Explicit, Size = 80)]
struct WinDivertAddress
{
    public const int OutboundBit = 17;
    public const int LoopbackBit = 18;
    public const int ImpostorBit = 19;
    public const int IPv6Bit = 20;

    [FieldOffset(0)] public long Timestamp;
    [FieldOffset(8)] public uint Bits;
    [FieldOffset(12)] public uint Reserved2;
    [FieldOffset(16)] public uint IfIdx;
    [FieldOffset(20)] public uint SubIfIdx;
    [FieldOffset(32)] public uint SocketProcessId;
    [FieldOffset(68)] public ushort SocketLocalPort;
    [FieldOffset(70)] public ushort SocketRemotePort;
    [FieldOffset(72)] public byte SocketProtocol;

    public readonly int Event => (int)((Bits >> 8) & 0xFF);

    public readonly bool Outbound => (Bits & (1u << OutboundBit)) != 0;

    public void SetOutbound(bool value) => SetBit(OutboundBit, value);

    public void SetLoopback(bool value) => SetBit(LoopbackBit, value);

    public void SetImpostor(bool value) => SetBit(ImpostorBit, value);

    public readonly bool IPv6 => (Bits & (1u << IPv6Bit)) != 0;

    void SetBit(int bit, bool value)
    {
        var mask = 1u << bit;
        Bits = value ? Bits | mask : Bits & ~mask;
    }
}
