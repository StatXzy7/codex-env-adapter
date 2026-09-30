using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexEnvAdapter;

static class AppProxy
{
    public const string DefaultServer = "127.0.0.1:7890";

    public static bool TryNormalize(string? text, out string hostPort)
    {
        hostPort = "";
        var value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            value = DefaultServer;
        }

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            value = value["http://".Length..];
        }
        else if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = value["https://".Length..];
        }

        value = value.TrimEnd('/');
        if (!Regex.IsMatch(value, @"^[A-Za-z0-9._-]{1,253}:\d{1,5}$"))
        {
            return false;
        }

        var portText = value[(value.LastIndexOf(':') + 1)..];
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            return false;
        }

        hostPort = value;
        return true;
    }

    public static string ToHttpUrl(string hostPort) => "http://" + hostPort;
}

static class CliProxy
{
    public static string BinDirectory => Path.Combine(SettingsStore.DirectoryPath, "bin");

    public static string Sync(bool installCliEntry, string? proxyHostPort)
    {
        Directory.CreateDirectory(BinDirectory);
        WriteProxyEnv(proxyHostPort);
        var firefox = FirefoxProxy.Apply(proxyHostPort);

        if (!installCliEntry)
        {
            DeleteShim("codex.cmd");
            DeleteShim("claude.cmd");
            SetUserPathEntry(false);
            return "已关闭终端入口。codex / claude 不再由启动器改代理。" + " " + firefox;
        }

        var notes = new List<string>();
        var codex = ResolveTool(
            "codex",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd"));
        var claude = ResolveTool(
            "claude",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"));

        if (codex is null)
        {
            DeleteShim("codex.cmd");
            notes.Add("未找到 codex");
        }
        else
        {
            WriteShim("codex.cmd", codex);
            notes.Add("codex");
        }

        if (claude is null)
        {
            DeleteShim("claude.cmd");
            notes.Add("未找到 claude");
        }
        else
        {
            WriteShim("claude.cmd", claude);
            notes.Add("claude");
        }

        if (codex is null && claude is null)
        {
            SetUserPathEntry(false);
            return "未找到 Codex CLI 或 Claude Code，没有写入终端入口。" + " " + firefox;
        }

        SetUserPathEntry(true);
        var mode = proxyHostPort is null ? "直连" : AppProxy.ToHttpUrl(proxyHostPort);
        return "终端入口已更新（" + mode + "）：" + string.Join("、", notes) + "。新开的终端才会用到。" + " " + firefox;
    }

    static void WriteProxyEnv(string? proxyHostPort)
    {
        var builder = new StringBuilder();
        builder.AppendLine("@echo off");
        if (proxyHostPort is null)
        {
            foreach (var name in ProxyEnvironment.Names)
            {
                builder.Append("set \"").Append(name).AppendLine("=\"");
            }
        }
        else
        {
            var url = AppProxy.ToHttpUrl(proxyHostPort);
            builder.Append("set \"HTTP_PROXY=").Append(url).AppendLine("\"");
            builder.Append("set \"HTTPS_PROXY=").Append(url).AppendLine("\"");
            builder.Append("set \"ALL_PROXY=").Append(url).AppendLine("\"");
            builder.Append("set \"NO_PROXY=").Append(ProxyEnvironment.NoProxy).AppendLine("\"");
            builder.AppendLine("set \"http_proxy=%HTTP_PROXY%\"");
            builder.AppendLine("set \"https_proxy=%HTTPS_PROXY%\"");
            builder.AppendLine("set \"all_proxy=%ALL_PROXY%\"");
            builder.AppendLine("set \"no_proxy=%NO_PROXY%\"");
        }

        File.WriteAllText(Path.Combine(BinDirectory, "proxy-env.cmd"), builder.ToString(), Encoding.ASCII);
    }

    static void WriteShim(string fileName, string target)
    {
        if (target.IndexOfAny(['"', '%', '&', '^']) >= 0)
        {
            throw new InvalidOperationException("程序路径含有不能写入命令入口的字符：" + target);
        }

        var body = "@echo off\r\ncall \"%~dp0proxy-env.cmd\"\r\n\"" + target + "\" %*\r\n";
        File.WriteAllText(Path.Combine(BinDirectory, fileName), body, Encoding.ASCII);
    }

    static void DeleteShim(string fileName)
    {
        var path = Path.Combine(BinDirectory, fileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    static string? ResolveTool(string name, params string[] fallbacks)
    {
        var found = new List<string>();
        try
        {
            var start = new ProcessStartInfo("where.exe", name)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(start);
            if (process is not null)
            {
                var text = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);
                found.AddRange(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }
        catch
        {
            // where.exe is only a hint; fallbacks below still apply.
        }

        found.AddRange(fallbacks);
        var matches = found
            .Where(path => File.Exists(path) && !IsUnderBin(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return matches.FirstOrDefault(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault(path => path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault();
    }

    static bool IsUnderBin(string path)
    {
        var full = Path.GetFullPath(path);
        var bin = Path.GetFullPath(BinDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(bin, StringComparison.OrdinalIgnoreCase);
    }

    static void SetUserPathEntry(bool present)
    {
        var current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        var bin = Path.GetFullPath(BinDirectory).TrimEnd('\\');
        var parts = current
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.Equals(part.TrimEnd('\\'), bin, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (present)
        {
            parts.Insert(0, bin);
        }

        Environment.SetEnvironmentVariable("Path", string.Join(';', parts), EnvironmentVariableTarget.User);
        BroadcastEnvironment();
    }

    public static void BroadcastEnvironment()
    {
        SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 2, 5000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);
}

static class ProxyEnvironment
{
    public static readonly string[] Names = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy"];

    public const string NoProxy = "localhost,127.0.0.1,::1";

    public static bool IsProxyEntry(string entry)
    {
        var split = entry.IndexOf('=');
        if (split <= 0)
        {
            return false;
        }

        var name = entry[..split];
        return Names.Any(candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }
}
