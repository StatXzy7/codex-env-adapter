using System.Net.Http.Json;

namespace CodexEnvAdapter;

static class NodeProbe
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexEnvAdapter/1.0");
        return client;
    }

    public static async Task<NodeInfo> DetectAsync(CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var url in new[] { "https://ipinfo.io/json", "https://ipapi.co/json/" })
        {
            try
            {
                var dto = await Http.GetFromJsonAsync<IpLookupDto>(url, cancellationToken).ConfigureAwait(false);
                var timezone = dto?.Timezone?.Trim();
                if (dto is null || string.IsNullOrWhiteSpace(timezone))
                {
                    continue;
                }

                var country = string.IsNullOrWhiteSpace(dto.Country) ? dto.CountryCode : dto.Country;
                return new NodeInfo
                {
                    PublicIp = dto.Ip ?? "",
                    City = dto.City ?? "",
                    Region = dto.Region ?? "",
                    Country = country ?? "",
                    Org = dto.Org ?? "",
                    Timezone = timezone,
                    Source = url,
                    DetectedAt = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                errors.Add($"{url}: {ex.Message}");
            }
        }

        throw new InvalidOperationException("无法探测出口节点。" + string.Join(" | ", errors));
    }
}
