using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Nethereum.Signer;
using PolymtkCp.Services.Secrets;

namespace PolymtkCp.Services.Executor;

/// <summary>
/// Client for the Polymarket CTF Exchange (CLOB).
///
/// <para>For each order, the client:
/// <list type="number">
///   <item>Looks up the market's tick size and neg-risk flag (public endpoints).</item>
///   <item>Computes integer maker / taker amounts in 6-decimal USDC units.</item>
///   <item>EIP-712-signs the resulting <c>Order</c> struct with the Follower's wallet private key.</item>
///   <item>Builds the L2 HMAC headers from the saved API credentials.</item>
///   <item>POSTs to <c>/order</c> and parses the response.</item>
/// </list></para>
/// </summary>
public sealed class PolymarketClobClient
{
    private static readonly JsonSerializerOptions OrderJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly HttpClient _http;
    private readonly ExecutorOptions _options;
    private readonly ILogger<PolymarketClobClient> _logger;

    public PolymarketClobClient(
        HttpClient http,
        IOptions<ExecutorOptions> options,
        ILogger<PolymarketClobClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<OrderPlacementResult> PlaceOrderAsync(
        OrderRequest order,
        PolymarketCredentials creds,
        string fallbackFunderAddress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(creds.PrivateKey))
            return new(false, null, "Wallet private key missing — required to EIP-712-sign the order.");

        var funder = !string.IsNullOrEmpty(creds.FunderAddress) ? creds.FunderAddress : fallbackFunderAddress;
        if (string.IsNullOrEmpty(funder))
            return new(false, null, "No funder address configured (neither in credentials nor profile wallet).");

        try
        {
            // 1. Discover market parameters. Both endpoints are public; the executor pays
            //    the round-trip per order — fine for low-volume copy-trading.
            var (tickSize, negRisk) = await GetMarketMetaAsync(order.Asset, ct);

            // 2. Compute amounts and assemble the unsigned order.
            var (makerAmount, takerAmount) =
                PolymarketOrderAmounts.Compute(order.Side, order.Price, order.SizeShares, tickSize);
            if (makerAmount == 0 || takerAmount == 0)
                return new(false, null,
                    $"Computed order amounts rounded to zero (price={order.Price}, size={order.SizeShares}, tick={tickSize}).");

            var signerAddress = new EthECKey(creds.PrivateKey).GetPublicAddress();
            var sigType = (byte)Math.Clamp(creds.SignatureType, 0, 2);
            byte sideByte = order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? (byte)0 : (byte)1;

            var unsigned = new SignedOrderData(
                Salt: PolymarketOrderSigner.NewSalt(),
                Maker: funder,
                Signer: signerAddress,
                Taker: "0x0000000000000000000000000000000000000000",
                TokenId: BigInteger.Parse(order.Asset),
                MakerAmount: makerAmount,
                TakerAmount: takerAmount,
                Expiration: 0,
                Nonce: 0,
                FeeRateBps: 0,
                Side: sideByte,
                SignatureType: sigType);

            // 3. Sign.
            var signature = PolymarketOrderSigner.Sign(unsigned, negRisk, _options.ChainId, creds.PrivateKey);

            // 4. Compose the request body Polymarket expects: { order: {...}, owner, orderType }.
            var body = new
            {
                order = new
                {
                    salt = unsigned.Salt.ToString(),
                    maker = unsigned.Maker,
                    signer = unsigned.Signer,
                    taker = unsigned.Taker,
                    tokenId = unsigned.TokenId.ToString(),
                    makerAmount = unsigned.MakerAmount.ToString(),
                    takerAmount = unsigned.TakerAmount.ToString(),
                    side = order.Side.ToUpperInvariant(),
                    expiration = unsigned.Expiration.ToString(),
                    nonce = unsigned.Nonce.ToString(),
                    feeRateBps = unsigned.FeeRateBps.ToString(),
                    signatureType = (int)unsigned.SignatureType,
                    signature,
                },
                owner = creds.ApiKey,
                orderType = "GTC",
            };
            var serialized = JsonSerializer.Serialize(body, OrderJsonOptions);

            // 5. Sign the HTTP request itself with the L2 HMAC headers.
            using var req = new HttpRequestMessage(HttpMethod.Post, "/order")
            {
                Content = new StringContent(serialized, Encoding.UTF8, "application/json"),
            };
            ApplyL2Headers(req, creds, funder, "POST", "/order", serialized);

            using var resp = await _http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "CLOB rejected order asset={Asset} side={Side} size={Size}: {Status} {Body}",
                    order.Asset, order.Side, order.SizeShares, (int)resp.StatusCode, Truncate(raw, 400));
                return new(false, null, $"CLOB {(int)resp.StatusCode}: {Truncate(raw, 200)}");
            }

            var orderId = TryExtractOrderId(raw) ?? "(no-id)";
            return new(true, orderId, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLOB order placement crashed for asset {Asset}.", order.Asset);
            return new(false, null, $"Exception: {ex.GetType().Name}: {Truncate(ex.Message, 200)}");
        }
    }

    private async Task<(string TickSize, bool NegRisk)> GetMarketMetaAsync(string tokenId, CancellationToken ct)
    {
        // /tick-size?token_id=... -> { "minimum_tick_size": "0.01" }
        // /neg-risk?token_id=...  -> { "neg_risk": false }
        // Both are public; the executor's HttpClient already targets the CLOB host.
        var tick = "0.01";
        var negRisk = false;

        try
        {
            using var resp = await _http.GetAsync($"/tick-size?token_id={tokenId}", ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("minimum_tick_size", out var t))
                    tick = t.GetString() ?? tick;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Tick-size lookup failed for {Token}; using default.", tokenId); }

        try
        {
            using var resp = await _http.GetAsync($"/neg-risk?token_id={tokenId}", ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("neg_risk", out var n))
                    negRisk = n.GetBoolean();
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Neg-risk lookup failed for {Token}; using default.", tokenId); }

        return (tick, negRisk);
    }

    private static void ApplyL2Headers(HttpRequestMessage req, PolymarketCredentials creds,
        string funderAddress, string method, string path, string body)
    {
        // Per py-clob-client signing/hmac.py:
        //   secret = base64-urlsafe-decode(creds.Secret)
        //   message = timestamp + method + requestPath + body  (with "'" replaced by '"')
        //   signature = base64-urlsafe-encode(HMAC-SHA256(secret, message))
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var normalizedBody = body.Replace("'", "\"");
        var message = timestamp + method + path + normalizedBody;

        var key = Base64UrlDecode(creds.Secret);
        using var hmac = new HMACSHA256(key);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        var signature = Base64UrlEncode(digest);

        req.Headers.TryAddWithoutValidation("POLY_ADDRESS", funderAddress);
        req.Headers.TryAddWithoutValidation("POLY_SIGNATURE", signature);
        req.Headers.TryAddWithoutValidation("POLY_TIMESTAMP", timestamp);
        req.Headers.TryAddWithoutValidation("POLY_API_KEY", creds.ApiKey);
        req.Headers.TryAddWithoutValidation("POLY_PASSPHRASE", creds.Passphrase);
    }

    private static byte[] Base64UrlDecode(string input)
    {
        // urlsafe variant uses '-' and '_' instead of '+' and '/'.
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');

    private static string? TryExtractOrderId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var key in new[] { "orderID", "orderId", "id" })
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
        }
        catch { /* not JSON or unexpected shape — fine */ }
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
