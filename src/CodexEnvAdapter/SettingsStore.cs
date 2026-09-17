using System.Text.Json;
using Microsoft.Win32;

namespace CodexEnvAdapter;

static class SettingsStore
{
    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexEnvAdapter");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        settings.UpdatedAt = DateTime.Now.ToString("s");
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}

static class WindowsEnv
{
    public static string GetSystemTimezoneId() => TimeZoneInfo.Local.Id;

    public static string GetSystemTimezoneDisplay()
    {
        var tz = TimeZoneInfo.Local;
        var offset = tz.BaseUtcOffset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{tz.Id} (UTC{sign}{offset:hh\\:mm})";
    }

    public static ProxyStatus GetProxyStatus()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key is null)
        {
            return new ProxyStatus();
        }

        var enabled = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0;
        var server = Convert.ToString(key.GetValue("ProxyServer")) ?? "";
        return new ProxyStatus
        {
            UserProxyEnable = enabled,
            UserProxyServer = server
        };
    }
}
