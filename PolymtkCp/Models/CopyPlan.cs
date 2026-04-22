using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace PolymtkCp.Models;

/// <summary>
/// A Follower's configuration for copying a single Trader.
/// One row per (Follower, Trader) pair.
/// </summary>
[Table("copy_plans")]
public class CopyPlan : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    /// <summary>Supabase auth user id of the Follower who owns this plan.</summary>
    [Column("follower_id")]
    public Guid FollowerId { get; set; }

    /// <summary>FK into <see cref="Trader"/>.</summary>
    [Column("trader_id")]
    public Guid TraderId { get; set; }

    /// <summary>"paper" (simulated only) or "real" (orders submitted to the CLOB).</summary>
    [Column("mode")]
    public string Mode { get; set; } = "paper";

    /// <summary>"fixed" or "percent".</summary>
    [Column("sizing_mode")]
    public string SizingMode { get; set; } = "fixed";

    /// <summary>USDC amount per copied trade when SizingMode = fixed.</summary>
    [Column("fixed_amount_usd")]
    public decimal? FixedAmountUsd { get; set; }

    /// <summary>0–100, share of the Trader's notional copied when SizingMode = percent.</summary>
    [Column("percent_of_notional")]
    public decimal? PercentOfNotional { get; set; }

    /// <summary>Hard cap on copied operations per UTC day. Null = no operation cap.</summary>
    [Column("daily_trade_operations_limit")]
    public int? DailyTradeOperationsLimit { get; set; }

    /// <summary>Hard cap on USDC spent on copied trades per UTC day. Null = no money cap.</summary>
    [Column("daily_trade_money_limit")]
    public decimal? DailyTradeMoneyLimit { get; set; }

    /// <summary>
    /// If set (&gt;= 2), collapse runs of adjacent same-(asset, side) fills into chunks of N;
    /// only the first fill in each chunk emits a simulated copy-trade (sized normally), and
    /// the rest are recorded as skipped with reason='grouped'. Null = no grouping.
    /// </summary>
    [Column("group_similar_ops")]
    public int? GroupSimilarOps { get; set; }

    /// <summary>When the plan stops copying. Null = never expires.</summary>
    [Column("expires_at")]
    public DateTime? ExpiresAt { get; set; }

    /// <summary>If false, the plan is paused and no copy-trades are emitted.</summary>
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at", ignoreOnInsert: true, ignoreOnUpdate: true)]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at", ignoreOnInsert: true, ignoreOnUpdate: true)]
    public DateTime UpdatedAt { get; set; }
}
