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

        if (args.Length >= 2 && args[0].Equals("--launch-tz", StringComparison.OrdinalIgnoreCase))
        {
            var install = ChatGptLauncher.FindInstall();
            if (install.RunningCount > 0)
            {
                ChatGptLauncher.StopRunning(TimeSpan.FromSeconds(15));
            }

            var proc = ChatGptLauncher.StartWithTimezone(install, args[1]);
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
}
