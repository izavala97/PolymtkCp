using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace PolymtkCp.Models;

/// <summary>
/// One row per copy-trade decision emitted by the watcher.
/// In phase 1 every row is <c>Mode = "paper"</c> (no order placed).
/// </summary>
[Table("copy_trade_executions")]
public class CopyTradeExecution : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("copy_plan_id")]
    public Guid CopyPlanId { get; set; }

    /// <summary>Denormalized from the parent CopyPlan for RLS without joins.</summary>
    [Column("follower_id")]
    public Guid FollowerId { get; set; }

    /// <summary>"paper" (phase 1) or "real" (phase 2).</summary>
    [Column("mode")]
    public string Mode { get; set; } = "paper";

    /// <summary>simulated | skipped | pending | submitted | filled | failed.</summary>
    [Column("status")]
    public string Status { get; set; } = "simulated";

    /// <summary>Hash of the source trade on the Trader's wallet — dedup key.</summary>
    [Column("source_activity_hash")]
    public string SourceActivityHash { get; set; } = string.Empty;

    [Column("source_timestamp")]
    public DateTime SourceTimestamp { get; set; }

    /// <summary>CLOB token id.</summary>
    [Column("asset")]
    public string Asset { get; set; } = string.Empty;

    [Column("condition_id")]
    public string? ConditionId { get; set; }

    /// <summary>"BUY" or "SELL".</summary>
    [Column("side")]
    public string Side { get; set; } = "BUY";

    [Column("price")]
    public decimal Price { get; set; }

    [Column("size_shares")]
    public decimal SizeShares { get; set; }

    [Column("size_usdc")]
    public decimal SizeUsdc { get; set; }

    /// <summary>Trader's original fill price. Preserved for future P&amp;L reconstruction / backtests.</summary>
    [Column("source_price")]
    public decimal? SourcePrice { get; set; }

    /// <summary>Trader's original fill size in shares.</summary>
    [Column("source_size_shares")]
    public decimal? SourceSizeShares { get; set; }

    /// <summary>Trader's original fill notional in USDC.</summary>
    [Column("source_size_usdc")]
    public decimal? SourceSizeUsdc { get; set; }

    [Column("event_title")]
    public string? EventTitle { get; set; }

    [Column("outcome")]
    public string? Outcome { get; set; }

    [Column("slug")]
    public string? Slug { get; set; }

    /// <summary>Parent event slug — used for the canonical <c>/event/{slug}</c> Polymarket URL.</summary>
    [Column("event_slug")]
    public string? EventSlug { get; set; }

    /// <summary>Free-text reason when Status is "skipped" or "failed".</summary>
    [Column("reason")]
    public string? Reason { get; set; }

    /// <summary>When the order was actually placed (phase 2). Null for paper trades.</summary>
    [Column("executed_at")]
    public DateTime? ExecutedAt { get; set; }

    [Column("created_at", ignoreOnInsert: true, ignoreOnUpdate: true)]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at", ignoreOnInsert: true, ignoreOnUpdate: true)]
    public DateTime UpdatedAt { get; set; }
}
