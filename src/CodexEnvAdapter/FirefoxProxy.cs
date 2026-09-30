using System.Text;

namespace CodexEnvAdapter;

static class FirefoxProxy
{
    const string Begin = "// BEGIN CodexEnvAdapter firefox-proxy";
    const string End = "// END CodexEnvAdapter firefox-proxy";

    public static string Apply(string? proxyHostPort)
    {
        var profiles = FindProfileDirectories();
        if (profiles.Count == 0)
        {
            return "未找到 Firefox 配置。";
        }

        var changed = 0;
        foreach (var profile in profiles)
        {
            if (!File.Exists(Path.Combine(profile, "prefs.js")))
            {
                continue;
            }

            var block = proxyHostPort is null ? null : BuildBlock(proxyHostPort);
            if (block is null && !HasManagedBlock(profile))
            {
                continue;
            }

            WriteUserJs(profile, block ?? BuildResetBlock());
            ClearAltSvcCache(profile);
            changed++;
        }

        if (changed == 0)
        {
            return "Firefox 没有需要改的代理配置。";
        }

        return proxyHostPort is null
            ? "已恢复 Firefox 为系统代理。请重启 Firefox。"
            : "Firefox 已固定走 HTTP 代理 " + proxyHostPort + "，并关闭 HTTP/3 直连。请重启 Firefox。";
    }

    static string BuildBlock(string hostPort)
    {
        var split = hostPort.LastIndexOf(':');
        var host = hostPort[..split];
        var port = hostPort[(split + 1)..];
        var builder = new StringBuilder();
        builder.AppendLine(Begin);
        builder.AppendLine("// 不开 TUN 时，HTTP/3 和 DoH 会绕过系统代理直连。这里改成固定走本地 HTTP 代理。");
        Pref(builder, "network.proxy.type", "1");
        Pref(builder, "network.proxy.http", "\"" + host + "\"");
        Pref(builder, "network.proxy.http_port", port);
        Pref(builder, "network.proxy.ssl", "\"" + host + "\"");
        Pref(builder, "network.proxy.ssl_port", port);
        Pref(builder, "network.proxy.share_proxy_settings", "true");
        Pref(builder, "network.proxy.socks", "\"\"");
        Pref(builder, "network.proxy.socks_port", "0");
        Pref(builder, "network.proxy.socks_remote_dns", "false");
        Pref(builder, "network.proxy.no_proxies_on", "\"localhost, 127.0.0.1, [::1]\"");
        Pref(builder, "network.proxy.failover_direct", "false");
        Pref(builder, "network.proxy.fast_path_system_direct", "false");
        Pref(builder, "network.http.http3.enable", "false");
        Pref(builder, "network.http.dictionaries.enable", "false");
        builder.AppendLine("// Cloudflare Turnstile 在 Firefox 里默认要等点击才给存储权限，ChatGPT 的锁定检查会一直 403。");
        Pref(builder, "dom.storage_access.enabled", "true");
        Pref(builder, "dom.storage_access.auto_grants", "true");
        Pref(builder, "dom.storage_access.auto_grants.delayed", "false");
        Pref(builder, "dom.storage_access.auto_grants.exclude_third_party_trackers", "false");
        Pref(builder, "network.http.altsvc.enabled", "false");
        Pref(builder, "network.http.altsvc.oe", "false");
        Pref(builder, "network.dns.echconfig.enabled", "true");
        Pref(builder, "network.trr.mode", "5");
        Pref(builder, "network.dns.disableIPv6", "true");
        Pref(builder, "network.dns.disablePrefetch", "true");
        Pref(builder, "network.dns.disablePrefetchFromHTTPS", "true");
        Pref(builder, "network.prefetch-next", "false");
        Pref(builder, "media.peerconnection.ice.proxy_only", "false");
        Pref(builder, "media.peerconnection.ice.proxy_only_if_behind_proxy", "false");
        builder.AppendLine(End);
        return builder.ToString();
    }

    static string BuildResetBlock()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Begin);
        Pref(builder, "network.proxy.type", "5");
        Pref(builder, "network.proxy.http", "\"\"");
        Pref(builder, "network.proxy.http_port", "0");
        Pref(builder, "network.proxy.ssl", "\"\"");
        Pref(builder, "network.proxy.ssl_port", "0");
        Pref(builder, "network.proxy.share_proxy_settings", "false");
        Pref(builder, "network.proxy.socks", "\"\"");
        Pref(builder, "network.proxy.socks_port", "0");
        Pref(builder, "network.proxy.socks_remote_dns", "false");
        Pref(builder, "network.proxy.no_proxies_on", "\"localhost, 127.0.0.1\"");
        Pref(builder, "network.proxy.failover_direct", "true");
        Pref(builder, "network.proxy.fast_path_system_direct", "true");
        Pref(builder, "network.http.http3.enable", "true");
        Pref(builder, "network.http.dictionaries.enable", "false");
        Pref(builder, "dom.storage_access.auto_grants.delayed", "true");
        Pref(builder, "dom.storage_access.auto_grants.exclude_third_party_trackers", "true");
        Pref(builder, "network.http.altsvc.enabled", "true");
        Pref(builder, "network.http.altsvc.oe", "false");
        Pref(builder, "network.dns.echconfig.enabled", "true");
        Pref(builder, "network.trr.mode", "0");
        Pref(builder, "network.dns.disableIPv6", "false");
        Pref(builder, "network.dns.disablePrefetch", "false");
        Pref(builder, "network.dns.disablePrefetchFromHTTPS", "false");
        Pref(builder, "network.prefetch-next", "true");
        Pref(builder, "media.peerconnection.ice.proxy_only", "false");
        Pref(builder, "media.peerconnection.ice.proxy_only_if_behind_proxy", "false");
        builder.AppendLine(End);
        return builder.ToString();
    }

    static void Pref(StringBuilder builder, string name, string value)
    {
        builder.Append("user_pref(\"").Append(name).Append("\", ").Append(value).AppendLine(");");
    }

    static void WriteUserJs(string profile, string block)
    {
        var path = Path.Combine(profile, "user.js");
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        File.WriteAllText(path, Merge(existing, block), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    static bool HasManagedBlock(string profile)
    {
        var path = Path.Combine(profile, "user.js");
        return File.Exists(path) && File.ReadAllText(path).Contains(Begin, StringComparison.Ordinal);
    }

    static string Merge(string existing, string block)
    {
        var begin = existing.IndexOf(Begin, StringComparison.Ordinal);
        var end = existing.IndexOf(End, StringComparison.Ordinal);
        if (begin >= 0 && end > begin)
        {
            var after = end + End.Length;
            var tail = existing[after..].TrimStart('\r', '\n');
            var head = existing[..begin].TrimEnd('\r', '\n');
            if (head.Length == 0)
            {
                return tail.Length == 0 ? block : block + "\r\n" + tail;
            }

            return tail.Length == 0 ? head + "\r\n\r\n" + block : head + "\r\n\r\n" + block + "\r\n" + tail;
        }

        if (string.IsNullOrWhiteSpace(existing))
        {
            return block;
        }

        return existing.TrimEnd('\r', '\n') + "\r\n\r\n" + block;
    }

    static void ClearAltSvcCache(string profile)
    {
        foreach (var name in new[] { "AlternateServices.txt", "AlternateServices.bin" })
        {
            var path = Path.Combine(profile, name);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Firefox may still hold the cache. The prefs above stop it being used.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    static List<string> FindProfileDirectories()
    {
        var iniPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mozilla",
            "Firefox",
            "profiles.ini");
        if (!File.Exists(iniPath))
        {
            return [];
        }

        var root = Path.GetDirectoryName(iniPath)!;
        var result = new List<string>();
        var inProfile = false;
        var isRelative = true;
        string? path = null;

        void Flush()
        {
            if (!inProfile || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var full = isRelative
                ? Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))
                : path;
            if (Directory.Exists(full))
            {
                result.Add(full);
            }
        }

        foreach (var raw in File.ReadAllLines(iniPath))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                inProfile = line.StartsWith("[Profile", StringComparison.OrdinalIgnoreCase);
                isRelative = true;
                path = null;
                continue;
            }

            if (!inProfile)
            {
                continue;
            }

            if (line.StartsWith("IsRelative=", StringComparison.OrdinalIgnoreCase))
            {
                isRelative = line.EndsWith('1');
            }
            else if (line.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
            {
                path = line["Path=".Length..];
            }
        }

        Flush();
        return result;
    }
}
