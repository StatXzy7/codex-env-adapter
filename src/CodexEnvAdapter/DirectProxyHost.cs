using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace CodexEnvAdapter;

static class DirectProxyHost
{
    const string StopName = "Local\\CodexEnvAdapter.DirectProxy.Stop";

    public static bool IsRequest(string[] args) =>
        args.Length > 0 && args[0].Equals("--direct-proxy", StringComparison.OrdinalIgnoreCase);

    public static string StatusPath => Path.Combine(SettingsStore.DirectoryPath, "direct-proxy.status");

    public static string LogPath => Path.Combine(SettingsStore.DirectoryPath, "direct-proxy.log");

    public static int Run(string[] args)
    {
        AttachConsole(-1);
        Directory.CreateDirectory(SettingsStore.DirectoryPath);
        var proxy = Arg(args, "--proxy");
        var install = Arg(args, "--install");
        int.TryParse(Arg(args, "--watch-pid"), out var watchPid);
        int.TryParse(Arg(args, "--only-pid"), out var onlyPid);
        if (!AppProxy.TryNormalize(proxy, out var hostPort))
        {
            WriteStatus("error", proxy, "代理地址无效。");
            return 1;
        }

        var split = hostPort.LastIndexOf(':');
        var host = hostPort[..split];
        var port = int.Parse(hostPort[(split + 1)..]);
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName);
        if (stop.WaitOne(0))
        {
            WriteStatus("error", hostPort, "接管在启动前被取消。");
            return 1;
        }

        WriteStatus("starting", hostPort, "正在接管 Codex 直连。");
        DirectProxyWorker? worker = null;
        try
        {
            worker = DirectProxyWorker.Start(new DirectProxyOptions
            {
                ProxyHost = host,
                ProxyPort = port,
                InstallLocation = install,
                WatchPid = watchPid,
                OnlyPid = onlyPid,
                Log = AppendLog
            });
            if (!worker.WaitReady(TimeSpan.FromSeconds(8)) || worker.Fault != null)
            {
                WriteStatus("error", hostPort, worker.Fault?.Message ?? "直连接管没有就绪。");
                return 1;
            }

            WriteStatus("ready", hostPort, "Codex 直连会转进 " + hostPort + "。Codex 退出后自动停止。");
            var sawChatGpt = false;
            var quiet = 0;
            var started = Environment.TickCount64;
            while (!stop.WaitOne(1000))
            {
                if (worker.Fault != null)
                {
                    WriteStatus("error", hostPort, worker.Fault.Message);
                    return 1;
                }

                var running = Process.GetProcessesByName("ChatGPT").Length > 0;
                if (running)
                {
                    sawChatGpt = true;
                    quiet = 0;
                    continue;
                }

                if (!sawChatGpt)
                {
                    if (Environment.TickCount64 - started > 90_000)
                    {
                        break;
                    }

                    continue;
                }

                if (++quiet >= 5)
                {
                    break;
                }
            }

            AppendLog("直连接管已停止。");
            WriteStatus("stopped", hostPort, "直连接管已停止。");
            return 0;
        }
        catch (Exception ex)
        {
            WriteStatus("error", hostPort, ex.Message);
            AppendLog(ex.ToString());
            return 1;
        }
        finally
        {
            worker?.Dispose();
        }
    }

    public static async Task<string> StartAsync(string proxyHostPort, string installLocation, CancellationToken token)
    {
        if (!AppProxy.TryNormalize(proxyHostPort, out var hostPort))
        {
            throw new InvalidOperationException("代理地址无效。请填写 127.0.0.1:7890 这样的地址。");
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            return "找不到启动器路径，这次不能接管 Codex 直连。";
        }

        Stop();
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName);
        stop.Reset();
        WriteStatus("starting", hostPort, "正在等待管理员授权。");
        var arguments = "--direct-proxy --proxy " + hostPort + " --install \"" + installLocation.Replace("\"", "", StringComparison.Ordinal) + "\"";
        try
        {
            await Task.Run(() =>
            {
                var admin = IsAdmin();
                var start = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    UseShellExecute = !admin
                };
                if (!admin)
                {
                    start.Verb = "runas";
                }
                else
                {
                    start.CreateNoWindow = true;
                }

                Process.Start(start);
            }, token);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            WriteStatus("error", hostPort, "已取消管理员授权。");
            return "已取消管理员授权。dots 连接这台电脑这次不会接管直连，其余功能仍会启动。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteStatus("error", hostPort, ex.Message);
            return "没能启动直连接管：" + ex.Message;
        }

        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var status = ReadStatus();
            if (status.TryGetValue("proxy", out var proxy) && !proxy.Equals(hostPort, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(200, token);
                continue;
            }

            if (status.TryGetValue("state", out var state) && state is "ready" or "error")
            {
                status.TryGetValue("message", out var message);
                return string.IsNullOrWhiteSpace(message) ? "直连接管状态：" + state : message;
            }

            await Task.Delay(200, token);
        }

        return "直连接管没有在 25 秒内就绪。Codex 仍会启动；若 dots 仍连不上，请再点一次启动并允许管理员授权。";
    }

    public static void Stop()
    {
        try
        {
            using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName);
            stop.Set();
        }
        catch
        {
            // The helper is already gone.
        }
    }

    static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static void AppendLog(string message)
    {
        try
        {
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine;
            File.AppendAllText(LogPath, line, Encoding.UTF8);
        }
        catch
        {
            // Logging must not break the redirector.
        }
    }

    static void WriteStatus(string state, string proxy, string message)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            var body = "state=" + state + "\nproxy=" + proxy + "\nmessage=" + message.Replace('\n', ' ') + "\n";
            var temp = StatusPath + ".tmp";
            File.WriteAllText(temp, body, Encoding.UTF8);
            File.Move(temp, StatusPath, overwrite: true);
        }
        catch
        {
            // The UI reports a timeout if the status file cannot be written.
        }
    }

    static Dictionary<string, string> ReadStatus()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(StatusPath))
            {
                return result;
            }

            foreach (var line in File.ReadAllLines(StatusPath))
            {
                var split = line.IndexOf('=');
                if (split > 0)
                {
                    result[line[..split]] = line[(split + 1)..];
                }
            }
        }
        catch
        {
            // A partial write is read again on the next poll.
        }

        return result;
    }

    static string Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return "";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
