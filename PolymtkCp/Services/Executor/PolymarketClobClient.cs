using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PolymtkCp.Services.Secrets;

namespace PolymtkCp.Services.Executor;

/// <summary>
/// Thin client for the Polymarket CLOB order endpoint.
///
/// <para><b>Status (April 2026):</b> the HTTP plumbing, credential plumbing,
/// and executor wiring are in place. The actual EIP-712 order signing +
/// L2 HMAC request signing are NOT yet implemented — a stub returns a
/// failure result with a clearly-marked reason so the
/// <see cref="OrderExecutor"/> hosted service can transition pending rows
/// to <c>failed</c> and surface the gap to the user, rather than silently
/// dropping orders.</para>
///
/// <para>Next steps to complete order placement:
/// <list type="number">
///   <item>Add a managed secp256k1 + EIP-712 dependency (e.g. Nethereum).</item>
///   <item>Implement <see cref="SignOrderAsync"/> per
///     <c>https://docs.polymarket.com/developers/CLOB/orders/orders</c>:
///     domain <c>name="Polymarket CTF Exchange", version="1", chainId=137,
///     verifyingContract=0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E</c>;
///     <c>Order</c> struct fields <c>(salt, maker, signer, taker, tokenId,
///     makerAmount, takerAmount, expiration, nonce, feeRateBps, side,
///     signatureType)</c>.
///   </item>
///   <item>Implement <see cref="BuildL2Headers"/>: HMAC-SHA256 over
///     <c>timestamp + method + requestPath + body</c> using the L2 secret
///     (base64-decoded), then base64-encode the digest into the
///     <c>POLY_SIGNATURE</c> header. Other headers: <c>POLY_API_KEY</c>,
///     <c>POLY_TIMESTAMP</c>, <c>POLY_PASSPHRASE</c>, <c>POLY_ADDRESS</c>.
///   </item>
///   <item>POST the signed order to <c>/order</c> and parse the response.</item>
/// </list></para>
/// </summary>
public sealed class PolymarketClobClient
{
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
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(creds.PrivateKey))
            return new(false, null, "Wallet private key missing — required to EIP-712-sign the order.");

        try
        {
            // Step 1: sign the order struct (EIP-712).
            var signedOrder = await SignOrderAsync(order, creds.PrivateKey!, ct);
            if (signedOrder is null)
                return new(false, null, "Order signing not yet implemented (CLOB EIP-712).");

            // Step 2: compose the request, attach L2 HMAC headers.
            using var req = new HttpRequestMessage(HttpMethod.Post, "/order")
            {
                Content = JsonContent.Create(signedOrder),
            };
            BuildL2Headers(req, creds, "POST", "/order", body: ""); // body hash TBD with signing

            // Step 3: send + parse.
            using var resp = await _http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "CLOB rejected order asset={Asset} side={Side}: {Status} {Body}",
                    order.Asset, order.Side, (int)resp.StatusCode, raw);
                return new(false, null, $"CLOB {(int)resp.StatusCode}: {Truncate(raw, 200)}");
            }

            // Tolerant parse: the CLOB returns { orderId, ... } on success;
            // grab whatever id-shaped field is there for traceability.
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

    /// <summary>
    /// Build and sign the CLOB <c>Order</c> struct.
    /// TODO(phase2-signing): implement EIP-712. Returning null causes the executor
    /// to mark the row as failed with a clear reason instead of silently dropping it.
    /// </summary>
    private Task<object?> SignOrderAsync(OrderRequest order, string privateKey, CancellationToken ct)
    {
        _logger.LogWarning(
            "PolymarketClobClient.SignOrderAsync is not implemented yet. " +
            "Order for asset={Asset} side={Side} size={Size} will be marked failed.",
            order.Asset, order.Side, order.SizeShares);
        return Task.FromResult<object?>(null);
    }

    /// <summary>
    /// Attach the L2 HMAC headers Polymarket requires on every authenticated CLOB request.
    /// TODO(phase2-signing): implement HMAC-SHA256 of <c>timestamp + method + path + body</c>.
    /// </summary>
    private static void BuildL2Headers(HttpRequestMessage req, PolymarketCredentials creds,
        string method, string path, string body)
    {
        // Placeholder — leave headers absent. The CLOB will reject the request, which
        // surfaces the missing implementation in the executor's failure-reason column.
        // Real implementation lands with SignOrderAsync.
        _ = req; _ = creds; _ = method; _ = path; _ = body;
    }

    private static string? TryExtractOrderId(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("orderId", out var idEl))
                return idEl.GetString();
            if (doc.RootElement.TryGetProperty("orderID", out idEl))
                return idEl.GetString();
            if (doc.RootElement.TryGetProperty("id", out idEl))
                return idEl.GetString();
        }
        catch { /* not JSON or unexpected shape — fine */ }
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
