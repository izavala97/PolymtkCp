using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace PolymtkCp.Services.Polymarket;

/// <summary>
/// Reads a wallet's USDC.e cash balance directly from the Polygon chain via
/// a public JSON-RPC endpoint. This is what Polymarket displays as "Cash".
/// USDC.e contract on Polygon: 0x2791Bca1f2de4661ED88A30C99A7a9449Aa84174 (6 decimals).
/// </summary>
public sealed class PolygonUsdcClient
{
    // 1rpc.io is a free, no-auth-required public Polygon RPC. Override via
    // the "Polygon:RpcUrl" config key for a paid endpoint in production.
    public const string DefaultRpcUrl = "https://1rpc.io/matic";

    // USDC.e (bridged USDC) on Polygon — the token Polymarket uses for cash.
    private const string UsdcContract = "0x2791Bca1f2de4661ED88A30C99A7a9449Aa84174";
    private const int UsdcDecimals = 6;

    // Function selector for ERC-20 balanceOf(address) — keccak256("balanceOf(address)")[..4]
    private const string BalanceOfSelector = "0x70a08231";

    private readonly HttpClient _http;
    private readonly ILogger<PolygonUsdcClient> _logger;

    public PolygonUsdcClient(HttpClient http, ILogger<PolygonUsdcClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>USDC cash balance for the given wallet, in dollars.</summary>
    public async Task<decimal> GetCashAsync(string walletAddress, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(walletAddress);

        if (!walletAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || walletAddress.Length != 42)
            throw new ArgumentException("Wallet address must be 0x + 40 hex chars.", nameof(walletAddress));

        // ABI-encode: selector + 32-byte left-padded address
        var data = BalanceOfSelector + new string('0', 24) + walletAddress[2..].ToLowerInvariant();

        var payload = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "eth_call",
            @params = new object[]
            {
                new { to = UsdcContract, data },
                "latest",
            },
        };

        try
        {
            using var resp = await _http.PostAsJsonAsync("", payload, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String)
                return 0m;

            var hex = result.GetString();
            if (string.IsNullOrEmpty(hex) || hex == "0x")
                return 0m;

            var raw = BigInteger.Parse("0" + hex[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            // Convert 6-decimal token units to dollars.
            return (decimal)raw / 1_000_000m;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Polygon RPC balanceOf failed for {Wallet}", walletAddress);
            throw;
        }
    }
}
