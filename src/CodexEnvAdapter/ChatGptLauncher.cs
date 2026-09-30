using System.Diagnostics;
using System.Text;

namespace CodexEnvAdapter;

static class ChatGptLauncher
{
    public static ChatGptInstall FindInstall()
    {
        var info = QueryPackage();
        var location = info.InstallLocation;
        if (string.IsNullOrWhiteSpace(location))
        {
            throw new InvalidOperationException("未找到已安装的 ChatGPT / Codex 桌面应用（包名 OpenAI.Codex）。");
        }

        var exe = Path.Combine(location, "app", "ChatGPT.exe");
        if (!File.Exists(exe))
        {
            throw new InvalidOperationException("未找到 ChatGPT.exe: " + exe);
        }

        return new ChatGptInstall
        {
            Version = info.Version,
            Exe = exe,
            InstallLocation = location,
            PackageFullName = info.PackageFullName,
            PackageFamilyName = info.PackageFamilyName,
            RunningCount = GetRunningCount()
        };
    }

    public static int GetRunningCount() => Process.GetProcessesByName("ChatGPT").Length;

    public static void StopRunning(TimeSpan timeout)
    {
        foreach (var proc in Process.GetProcessesByName("ChatGPT"))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort; the wait loop below is the real check.
            }
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (GetRunningCount() == 0)
            {
                return;
            }

            Thread.Sleep(400);
        }

        if (GetRunningCount() > 0)
        {
            throw new InvalidOperationException("ChatGPT 未能完全退出。请先点托盘图标退出后再试。");
        }
    }

    public static Process StartWithTimezone(ChatGptInstall install, string timezone, string? proxyHostPort = null) =>
        PackagedLaunch.Launch(install, timezone, proxyHostPort);

    public static void CreateDesktopShortcut(string targetPath)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var shortcutPath = Path.Combine(desktop, "Codex环境适配启动器.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("无法创建桌面快捷方式（缺少 WScript.Shell）。");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法创建 WScript.Shell。");
        var shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
        shortcut.WindowStyle = 1;
        shortcut.Description = "Codex环境适配启动器：按当前出口节点时区启动 ChatGPT / Codex";
        if (File.Exists(targetPath))
        {
            shortcut.IconLocation = targetPath + ",0";
        }

        shortcut.Save();
    }

    static PackageQuery QueryPackage()
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"$pkg = Get-AppxPackage -Name OpenAI.Codex | Select-Object -First 1; if ($pkg) { [pscustomobject]@{ InstallLocation = $pkg.InstallLocation; Version = [string]$pkg.Version; PackageFullName = $pkg.PackageFullName; PackageFamilyName = $pkg.PackageFamilyName } | ConvertTo-Json -Compress }\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var proc = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动 PowerShell 查询 Codex 安装路径。");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(15000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("查询 Codex 安装路径超时。");
        }

        var output = stdoutTask.GetAwaiter().GetResult().Trim().TrimStart('\uFEFF');
        stderrTask.GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(output))
        {
            return new PackageQuery();
        }

        var dto = System.Text.Json.JsonSerializer.Deserialize<PackageQuery>(output);
        return dto ?? new PackageQuery();
    }

    sealed class PackageQuery
    {
        public string InstallLocation { get; set; } = "";
        public string Version { get; set; } = "";
        public string PackageFullName { get; set; } = "";
        public string PackageFamilyName { get; set; } = "";
    }
}
