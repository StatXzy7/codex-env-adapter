using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexEnvAdapter;

static class PackagedLaunch
{
    const uint ProcessQueryInformation = 0x0400;
    const uint ProcessVmRead = 0x0010;
    const uint ProcessVmWrite = 0x0020;
    const uint ProcessVmOperation = 0x0008;

    public static bool IsInjectRequest(string[] args) =>
        args.Any(arg => arg.Equals("--inject-tz", StringComparison.OrdinalIgnoreCase));

    public static void InjectFromDebuggerArgs(string[] args)
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexEnvAdapter",
            "inject.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            if (!TryGetArg(args, "--inject-tz", out var timezone) || !IsSafeTimezone(timezone))
            {
                File.WriteAllText(logPath, "missing timezone\n");
                return;
            }

            if (!TryGetArg(args, "-p", out var pidText) || !int.TryParse(pidText, out var pid))
            {
                File.WriteAllText(logPath, "missing pid\n");
                return;
            }

            string? proxyHostPort = null;
            var manageProxy = false;
            if (TryGetArg(args, "--inject-proxy", out var proxyArg))
            {
                manageProxy = true;
                if (!proxyArg.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    if (!AppProxy.TryNormalize(proxyArg, out var normalized))
                    {
                        File.WriteAllText(logPath, "bad proxy\n");
                        return;
                    }

                    proxyHostPort = normalized;
                }
            }

            var proxyNote = InjectTimezone(pid, timezone, manageProxy, proxyHostPort);
            File.WriteAllText(logPath, $"ok pid={pid} tz={timezone} {proxyNote}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(logPath, ex.ToString()); } catch { }
        }
    }

    public static Process Launch(ChatGptInstall install, string timezone, string? proxyHostPort)
    {
        if (!IsSafeTimezone(timezone))
        {
            throw new InvalidOperationException("时区名称无效。请使用 IANA 名称，例如 America/New_York。");
        }

        if (string.IsNullOrWhiteSpace(install.PackageFullName) || string.IsNullOrWhiteSpace(install.PackageFamilyName))
        {
            throw new InvalidOperationException("没有读到 Codex 的包标识，无法按新版本的方式启动。");
        }

        var self = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(self) || !File.Exists(self))
        {
            throw new InvalidOperationException("找不到启动器自身路径，无法注入时区。");
        }

        var proxyToken = string.IsNullOrWhiteSpace(proxyHostPort) ? "off" : proxyHostPort;
        var command = Quote(self) + " --inject-tz " + timezone + " --inject-proxy " + proxyToken;
        var debug = CreateDebugSettings();
        var enableHr = debug.EnableDebugging(install.PackageFullName, command, IntPtr.Zero);
        if (enableHr < 0)
        {
            throw new Win32Exception(enableHr, "无法进入 Codex 包启动上下文。");
        }

        Process? proc = null;
        try
        {
            var aumid = install.PackageFamilyName + "!App";
            // The package creates the command line. Patching it afterwards overflows the
            // original buffer (it has no spare room) and ChatGPT faults in ntdll while
            // converting that string. Activation arguments are included when the buffer is built.
            var arguments = ProxyArguments(proxyHostPort);
            var activateHr = CreateActivationManager().ActivateApplication(aumid, arguments, 0, out var pid);
            if (activateHr < 0 || pid == 0)
            {
                throw new Win32Exception(activateHr, "激活 Codex 失败。");
            }

            proc = Process.GetProcessById(unchecked((int)pid));
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                proc.Refresh();
                if (proc.HasExited)
                {
                    throw new InvalidOperationException("Codex 进程已退出。请确认没有残留的 ChatGPT 进程后再试。");
                }

                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    return proc;
                }

                Thread.Sleep(300);
            }

            throw new InvalidOperationException("Codex 已创建但没有出现窗口。");
        }
        catch
        {
            if (proc is not null)
            {
                try
                {
                    proc.Refresh();
                    if (!proc.HasExited && proc.MainWindowHandle == IntPtr.Zero)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // The caller reports the original failure.
                }
            }

            throw;
        }
        finally
        {
            try { debug.DisableDebugging(install.PackageFullName); } catch { }
        }
    }

    public static string InjectTimezone(int pid, string timezone, bool manageProxy = false, string? proxyHostPort = null)
    {
        using var proc = Process.GetProcessById(pid);
        if (proc.MainWindowHandle != IntPtr.Zero)
        {
            return "skipped-visible";
        }

        var access = ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation;
        var handle = OpenProcess(access, false, pid);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开 Codex 进程。");
        }

        try
        {
            var info = new byte[48];
            var status = NtQueryInformationProcess(handle, 0, info, info.Length, out _);
            if (status != 0)
            {
                throw new InvalidOperationException("读取进程信息失败，NTSTATUS=" + status);
            }

            var peb = BitConverter.ToInt64(info, 8);
            var parameters = ReadInt64(handle, peb + 0x20);
            var envAddress = ReadInt64(handle, parameters + 0x80);
            var envSize = ReadInt64(handle, parameters + 0x3F0);
            if (envAddress == 0 || envSize <= 0 || envSize > 1024 * 1024)
            {
                throw new InvalidOperationException("Codex 进程环境块不可用。");
            }

            var current = new byte[envSize];
            if (!ReadProcessMemory(handle, envAddress, current, current.Length, out var read) || read != current.Length)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "读取环境块失败。");
            }

            var entries = ParseEnvironment(current)
                .Where(entry => !entry.StartsWith("TZ=", StringComparison.OrdinalIgnoreCase))
                .Where(entry => !manageProxy || !ProxyEnvironment.IsProxyEntry(entry))
                .ToList();
            entries.Add("TZ=" + timezone);
            if (manageProxy && proxyHostPort is not null)
            {
                var url = AppProxy.ToHttpUrl(proxyHostPort);
                entries.Add("HTTP_PROXY=" + url);
                entries.Add("HTTPS_PROXY=" + url);
                entries.Add("ALL_PROXY=" + url);
                entries.Add("NO_PROXY=" + ProxyEnvironment.NoProxy);
            }

            entries.Sort(StringComparer.OrdinalIgnoreCase);
            var block = BuildEnvironment(entries);
            var target = envAddress;
            if (block.Length > current.Length)
            {
                target = VirtualAllocEx(handle, IntPtr.Zero, (nuint)block.Length, 0x3000, 0x04);
                if (target == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "分配环境块失败。");
                }

                WriteInt64(handle, parameters + 0x80, target);
            }

            if (!WriteProcessMemory(handle, target, block, block.Length, out var written) || written != block.Length)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "写入时区失败。");
            }

            WriteInt64(handle, parameters + 0x3F0, block.Length);
            ResumeProcessThreads(pid);
            if (!manageProxy)
            {
                return "proxy=inherit";
            }

            return proxyHostPort is null ? "proxy=off" : "proxy=" + proxyHostPort;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static string ProxyArguments(string? proxyHostPort)
    {
        if (string.IsNullOrWhiteSpace(proxyHostPort))
        {
            return "";
        }

        return "--proxy-server=" + AppProxy.ToHttpUrl(proxyHostPort) + " --proxy-bypass-list=<-loopback> --disable-quic";
    }

    static bool IsSafeTimezone(string timezone) =>
        Regex.IsMatch(timezone, "^[A-Za-z0-9_+\\-/]{1,64}$");

    static bool TryGetArg(string[] args, string name, out string value)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = args[i + 1];
                return true;
            }
        }

        value = "";
        return false;
    }

    static string Quote(string path) => "\"" + path.Replace("\"", "", StringComparison.Ordinal) + "\"";

    static List<string> ParseEnvironment(byte[] block)
    {
        var text = Encoding.Unicode.GetString(block);
        var list = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\0')
            {
                continue;
            }

            if (i == start)
            {
                break;
            }

            list.Add(text[start..i]);
            start = i + 1;
        }

        if (list.Count == 0 || !list[0].Contains('=', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("无法解析 Codex 启动环境。");
        }

        return list;
    }

    static byte[] BuildEnvironment(List<string> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(entry).Append('\0');
        }

        builder.Append('\0');
        return Encoding.Unicode.GetBytes(builder.ToString());
    }

    static long ReadInt64(IntPtr handle, long address)
    {
        var buffer = new byte[8];
        if (!ReadProcessMemory(handle, address, buffer, 8, out var read) || read != 8)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "读取进程内存失败。");
        }

        return BitConverter.ToInt64(buffer, 0);
    }

    static void WriteInt64(IntPtr handle, long address, long value)
    {
        var buffer = BitConverter.GetBytes(value);
        if (!WriteProcessMemory(handle, address, buffer, 8, out var written) || written != 8)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "写入进程内存失败。");
        }
    }

    static void ResumeProcessThreads(int pid)
    {
        var snapshot = CreateToolhelp32Snapshot(0x00000004, 0);
        if (snapshot == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "枚举线程失败。");
        }

        try
        {
            var entry = new ThreadEntry { dwSize = Marshal.SizeOf<ThreadEntry>() };
            if (!Thread32First(snapshot, ref entry))
            {
                return;
            }

            do
            {
                if (entry.th32OwnerProcessID != pid)
                {
                    continue;
                }

                var thread = OpenThread(0x0002, false, entry.th32ThreadID);
                if (thread == IntPtr.Zero)
                {
                    continue;
                }

                ResumeThread(thread);
                CloseHandle(thread);
            }
            while (Thread32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    static IPackageDebugSettings CreateDebugSettings()
    {
        var type = Type.GetTypeFromCLSID(new Guid("B1AEC16F-2383-4852-B0E9-8F0B1DC66B4D"))
            ?? throw new InvalidOperationException("系统不支持 Codex 包调试启动接口。");
        return (IPackageDebugSettings)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("无法创建 Codex 包启动接口。"));
    }

    static IApplicationActivationManager CreateActivationManager()
    {
        var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"))
            ?? throw new InvalidOperationException("系统不支持应用激活接口。");
        return (IApplicationActivationManager)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("无法创建应用激活接口。"));
    }

    [ComImport, Guid("F27C3930-8029-4AD1-94E3-3DBA417810C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPackageDebugSettings
    {
        [PreserveSig]
        int EnableDebugging(
            [MarshalAs(UnmanagedType.LPWStr)] string packageFullName,
            [MarshalAs(UnmanagedType.LPWStr)] string debuggerCommandLine,
            IntPtr environment);

        [PreserveSig]
        int DisableDebugging([MarshalAs(UnmanagedType.LPWStr)] string packageFullName);
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            int options,
            out uint processId);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ThreadEntry
    {
        public int dwSize;
        public int cntUsage;
        public int th32ThreadID;
        public int th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public int dwFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr handle, long address, byte[] buffer, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr handle, long address, byte[] buffer, int size, out int written);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern long VirtualAllocEx(IntPtr handle, IntPtr address, nuint size, uint allocationType, uint protect);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, byte[] info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenThread(uint access, bool inherit, int threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(IntPtr thread);
}
