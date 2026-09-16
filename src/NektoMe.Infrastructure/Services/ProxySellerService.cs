using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NektoMe.Application.Services;

namespace NektoMe.Infrastructure.Services;

public sealed class ProxySellerService
{
    private const string DefaultBaseUrl = "https://proxy-seller.com/personal/api/v1/";
    public static string DefaultApiKey => SettingsService.Load().ProxySellerApiKey;

    private readonly HttpClient _http;
    private readonly ILogger<ProxySellerService> _logger;

    public ProxySellerService(HttpClient? httpClient = null, ILogger<ProxySellerService>? logger = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _logger = logger ?? NullLogger<ProxySellerService>.Instance;
    }

    public async Task<ProxySellerResult> FetchResidentProxiesAsync(string? apiKey = null, CancellationToken ct = default)
    {
        string key = string.IsNullOrWhiteSpace(apiKey) ? DefaultApiKey : apiKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Ключ Proxy-Seller API не задан. Введите его в настройках.");
        }
        string baseUri = $"{DefaultBaseUrl}{key}/";

        // 1. Check Package
        string packageSummary = "Неизвестно";
        try
        {
            var pkgJson = await GetJsonAsync($"{baseUri}resident/package", ct).ConfigureAwait(false);
            ThrowIfApiError(pkgJson);
            if (pkgJson.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                bool isActive = data.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True;
                string expired = data.TryGetProperty("expired_at", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
                long trafficLeft = 0;
                if (data.TryGetProperty("traffic_left", out var tl))
                {
                    if (tl.ValueKind == JsonValueKind.Number) trafficLeft = tl.GetInt64();
                    else if (tl.ValueKind == JsonValueKind.String && long.TryParse(tl.GetString(), out var parsed)) trafficLeft = parsed;
                }
                double mb = trafficLeft / (1024.0 * 1024.0);
                string trafficStr = mb >= 1024 ? $"{mb / 1024.0:F2} ГБ" : $"{mb:F1} МБ";
                packageSummary = $"Резидентский пакет: {(isActive ? "Активен" : "Неактивен")}, трафик: {trafficStr}, до: {expired}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read resident package info");
        }

        // 2. Reuse existing list or create one if none exists.
        // Residential IPs rotate automatically every hour — no need to recreate lists.
        // Deleting IP-whitelisted lists breaks them; the underlying IPs change on their own.
        int listId = 0;
        try
        {
            var listsJson = await GetJsonAsync($"{baseUri}resident/lists", ct).ConfigureAwait(false);
            ThrowIfApiError(listsJson);
            if (listsJson.TryGetProperty("data", out var listArr) && listArr.ValueKind == JsonValueKind.Array)
            {
                // Prefer the app-owned "nektome_app" list (created with 20 ports);
                // older/manual lists (e.g. "nekto_all") may have fewer ports. The
                // first list of any title is kept as a fallback.
                foreach (var item in listArr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("id", out var idProp)
                        || idProp.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    string title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() ?? ""
                        : "";
                    if (listId == 0)
                    {
                        listId = idProp.GetInt32();
                    }

                    if (string.Equals(title, SettingsService.Load().ProxySellerListName, StringComparison.OrdinalIgnoreCase))
                    {
                        listId = idProp.GetInt32();
                        break;
                    }
                }
            }

            var settings = SettingsService.Load();
            if (listId == 0)
            {
                // No list exists — create one with configured ports
                var createPayload = new
                {
                    title = settings.ProxySellerListName,
                    whitelist = settings.ProxySellerWhitelistIp,
                    geo = new { },
                    export = new { ports = settings.ProxySellerPortsCount, ext = "txt" },
                    rotation = settings.ProxySellerRotationSeconds
                };
                string reqBody = JsonSerializer.Serialize(createPayload);
                using var content = new StringContent(reqBody, Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync($"{baseUri}resident/list/add", content, ct).ConfigureAwait(false);
                string respStr = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(respStr);
                ThrowIfApiError(doc.RootElement);
                if (doc.RootElement.TryGetProperty("data", out var created)
                    && created.ValueKind == JsonValueKind.Object
                    && created.TryGetProperty("id", out var idProp))
                {
                    listId = idProp.GetInt32();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch or create resident list");
            throw;
        }

        if (listId == 0)
        {
            throw new InvalidOperationException("Не удалось получить или создать список резидентских прокси.");
        }

        // 3. Download proxy list
        string downloadUrl = $"{baseUri}proxy/download/resident?listId={listId}";
        string txt;
        try
        {
            txt = await _http.GetStringAsync(downloadUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download proxy list {ListId}", listId);
            throw;
        }
        var lines = txt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var proxies = new List<string>();
        foreach (var line in lines)
        {
            if (line.Contains('@'))
            {
                string url = line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             line.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
                    ? line
                    : $"http://{line}";
                proxies.Add(url);
            }
        }

        if (proxies.Count == 0)
        {
            throw new InvalidOperationException("Proxy-Seller вернул пустой список прокси.");
        }

        // Natural sort by port/endpoint
        proxies.Sort(StringComparer.OrdinalIgnoreCase);

        return new ProxySellerResult(proxies, packageSummary, listId);
    }

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct)
    {
        string body = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Proxy-Seller API returns HTTP 200 even for failures:
    /// <c>{"status":"error","data":null,"errors":[{"message":"IP not allowed 1.2.3.4","code":503}]}</c>.
    /// Surface the real message instead of letting downstream JSON access throw
    /// a cryptic "target element has type 'Null'".
    /// </summary>
    private static void ThrowIfApiError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
            && string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            string message = "неизвестная ошибка";
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var e in errors.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(m.GetString() ?? "");
                    }
                }
                if (parts.Count > 0)
                {
                    message = string.Join("; ", parts);
                }
            }

            string hint = message.Contains("IP not allowed", StringComparison.OrdinalIgnoreCase)
                ? " → добавьте этот IP в белый список в личном кабинете Proxy-Seller (Personal → API), либо введите прокси вручную в поле 'Proxy'."
                : "";
            throw new ProxySellerApiException($"Proxy-Seller API: {message}{hint}");
        }
    }
}

public sealed record ProxySellerResult(List<string> Proxies, string PackageSummary, int ListId);

/// <summary>Proxy-Seller API returned a structured error envelope (status="error").</summary>
public sealed class ProxySellerApiException : Exception
{
    public ProxySellerApiException(string message) : base(message) { }
}
