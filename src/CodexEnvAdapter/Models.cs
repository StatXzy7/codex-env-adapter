using System.Text.Json.Serialization;

namespace CodexEnvAdapter;

sealed class NodeInfo
{
    public string PublicIp { get; init; } = "";
    public string City { get; init; } = "";
    public string Region { get; init; } = "";
    public string Country { get; init; } = "";
    public string Org { get; init; } = "";
    public string Timezone { get; init; } = "";
    public string Source { get; init; } = "";
    public DateTime DetectedAt { get; init; } = DateTime.Now;

    public string LocationText
    {
        get
        {
            var parts = new[] { City, Region, Country }.Where(x => !string.IsNullOrWhiteSpace(x));
            return string.Join(", ", parts);
        }
    }
}

sealed class ProxyStatus
{
    public bool UserProxyEnable { get; init; }
    public string UserProxyServer { get; init; } = "";
}

sealed class ChatGptInstall
{
    public string Version { get; init; } = "";
    public string Exe { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public int RunningCount { get; init; }
}

sealed class AppSettings
{
    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("autoTimezone")]
    public bool AutoTimezone { get; set; } = true;

    [JsonPropertyName("restartChatGpt")]
    public bool RestartChatGpt { get; set; } = true;

    [JsonPropertyName("publicIp")]
    public string? PublicIp { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("org")]
    public string? Org { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("systemTz")]
    public string? SystemTz { get; set; }

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }
}

sealed class IpLookupDto
{
    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("country_code")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("org")]
    public string? Org { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }
}
