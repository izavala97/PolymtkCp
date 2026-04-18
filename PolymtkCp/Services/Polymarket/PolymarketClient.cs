using System.Net;
using System.Text.Json;

namespace PolymtkCp.Services.Polymarket;

/// <summary>
/// Read-only client for Polymarket's public Data API.
/// No authentication required.
/// </summary>
public sealed class PolymarketClient
{
    public const string DataApiBaseUrl = "https://data-api.polymarket.com/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly ILogger<PolymarketClient> _logger;

    public PolymarketClient(HttpClient http, ILogger<PolymarketClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Fetch a wallet's currently held positions.
    /// </summary>
    public async Task<IReadOnlyList<PolymarketPosition>> GetPositionsAsync(
        string walletAddress, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(walletAddress);
        var url = $"positions?user={Uri.EscapeDataString(walletAddress)}&sizeThreshold=0";
        return await GetJsonArrayAsync<PolymarketPosition>(url, ct);
    }

    /// <summary>
    /// Fetch a wallet's recent activity (trades, redemptions).
    /// </summary>
    public async Task<IReadOnlyList<PolymarketActivity>> GetActivityAsync(
        string walletAddress, int limit = 50, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(walletAddress);
        var url = $"activity?user={Uri.EscapeDataString(walletAddress)}&limit={limit}";
        return await GetJsonArrayAsync<PolymarketActivity>(url, ct);
    }

    /// <summary>
    /// Fetch the total USDC value of all open positions for a wallet.
    /// Returns 0 if the wallet has no positions.
    /// </summary>
    public async Task<decimal> GetPortfolioValueAsync(
        string walletAddress, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(walletAddress);
        var url = $"value?user={Uri.EscapeDataString(walletAddress)}";

        try
        {
            using var resp = await _http.GetAsync(url, ct);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return 0m;

            resp.EnsureSuccessStatusCode();

            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var rows = await JsonSerializer.DeserializeAsync<List<PolymarketValue>>(stream, JsonOptions, ct);
            return rows?.FirstOrDefault()?.Value ?? 0m;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Polymarket /value call failed for {Wallet}", walletAddress);
            throw;
        }
    }

    private async Task<IReadOnlyList<T>> GetJsonArrayAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return [];

            resp.EnsureSuccessStatusCode();

            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var result = await JsonSerializer.DeserializeAsync<List<T>>(stream, JsonOptions, ct);
            return result ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Polymarket API call failed: {Url}", url);
            throw;
        }
    }
}
