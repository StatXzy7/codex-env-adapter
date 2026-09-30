namespace CodexEnvAdapter;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (PackagedLaunch.IsInjectRequest(args))
        {
            PackagedLaunch.InjectFromDebuggerArgs(args);
            return;
        }

        if (args.Length >= 1 && args[0].Equals("--sync-proxy", StringComparison.OrdinalIgnoreCase))
        {
            var settings = SettingsStore.Load();
            string? proxy = null;
            if (settings.AppProxyEnabled)
            {
                if (!AppProxy.TryNormalize(settings.AppProxyServer, out var hostPort))
                {
                    throw new InvalidOperationException("应用代理地址无效：" + settings.AppProxyServer);
                }

                proxy = hostPort;
            }

            var message = CliProxy.Sync(settings.CliProxyEnabled, proxy);
            var logPath = Path.Combine(SettingsStore.DirectoryPath, "sync-proxy.log");
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            File.WriteAllText(logPath, message);
            Console.WriteLine(message);
            return;
        }

        if (args.Length >= 2 && args[0].Equals("--launch-tz", StringComparison.OrdinalIgnoreCase))
        {
            var install = ChatGptLauncher.FindInstall();
            if (install.RunningCount > 0)
            {
                ChatGptLauncher.StopRunning(TimeSpan.FromSeconds(15));
            }

            var proc = ChatGptLauncher.StartWithTimezone(install, args[1], ProxyHostFromSettings());
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexEnvAdapter",
                "launch.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, proc.Id.ToString());
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    static string? ProxyHostFromSettings()
    {
        var settings = SettingsStore.Load();
        if (!settings.AppProxyEnabled)
        {
            return null;
        }

        if (!AppProxy.TryNormalize(settings.AppProxyServer, out var hostPort))
        {
            throw new InvalidOperationException("应用代理地址无效。请填写 127.0.0.1:7890 这样的地址。");
        }

        return hostPort;
    }
}
