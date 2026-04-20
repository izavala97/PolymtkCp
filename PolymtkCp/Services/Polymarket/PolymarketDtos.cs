using System.Text.Json.Serialization;

namespace PolymtkCp.Services.Polymarket;

/// <summary>
/// Single position held by a wallet. Returned by
/// <c>GET data-api.polymarket.com/positions?user={addr}</c>.
/// </summary>
public sealed class PolymarketPosition
{
    [JsonPropertyName("proxyWallet")]
    public string ProxyWallet { get; set; } = string.Empty;

    [JsonPropertyName("asset")]
    public string Asset { get; set; } = string.Empty; // CLOB token id

    [JsonPropertyName("conditionId")]
    public string ConditionId { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public decimal Size { get; set; }

    [JsonPropertyName("avgPrice")]
    public decimal AvgPrice { get; set; }

    [JsonPropertyName("initialValue")]
    public decimal InitialValue { get; set; }

    [JsonPropertyName("currentValue")]
    public decimal CurrentValue { get; set; }

    [JsonPropertyName("cashPnl")]
    public decimal CashPnl { get; set; }

    [JsonPropertyName("percentPnl")]
    public decimal PercentPnl { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    /// <summary>Parent event slug — the canonical Polymarket URL is <c>/event/{eventSlug}</c>.</summary>
    [JsonPropertyName("eventSlug")]
    public string? EventSlug { get; set; }

    [JsonPropertyName("redeemable")]
    public bool Redeemable { get; set; }

    [JsonPropertyName("endDate")]
    public DateTimeOffset? EndDate { get; set; }
}

/// <summary>
/// Single activity entry (trade, redeem, etc.). Returned by
/// <c>GET data-api.polymarket.com/activity?user={addr}</c>.
/// We model only the trade fields we currently care about.
/// </summary>
public sealed class PolymarketActivity
{
    [JsonPropertyName("proxyWallet")]
    public string ProxyWallet { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // TRADE, REDEEM, etc.

    [JsonPropertyName("side")]
    public string? Side { get; set; } // BUY / SELL

    [JsonPropertyName("asset")]
    public string? Asset { get; set; }

    [JsonPropertyName("conditionId")]
    public string? ConditionId { get; set; }

    [JsonPropertyName("size")]
    public decimal Size { get; set; }

    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("usdcSize")]
    public decimal UsdcSize { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    /// <summary>Parent event slug — the canonical Polymarket URL is <c>/event/{eventSlug}</c>.</summary>
    [JsonPropertyName("eventSlug")]
    public string? EventSlug { get; set; }

    [JsonPropertyName("transactionHash")]
    public string? TransactionHash { get; set; }

    [JsonPropertyName("timestamp")]
    public long TimestampUnix { get; set; }

    [JsonIgnore]
    public DateTimeOffset Timestamp =>
        DateTimeOffset.FromUnixTimeSeconds(TimestampUnix);
}

/// <summary>
/// Single row returned by <c>GET data-api.polymarket.com/value?user={addr}</c>.
/// Body is an array; we only need the value field.
/// </summary>
public sealed class PolymarketValue
{
    [JsonPropertyName("user")]
    public string User { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public decimal Value { get; set; }
}
