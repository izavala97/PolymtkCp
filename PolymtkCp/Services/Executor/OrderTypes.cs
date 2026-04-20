using PolymtkCp.Services.Secrets;

namespace PolymtkCp.Services.Executor;

/// <summary>
/// Result of a single order placement attempt against the Polymarket CLOB.
/// Surfaced by <see cref="PolymarketClobClient.PlaceOrderAsync"/> and used by
/// the <see cref="OrderExecutor"/> to update <c>copy_trade_executions</c>.
/// </summary>
/// <param name="Success">
/// True when the CLOB accepted the order. The row is then transitioned to
/// <c>submitted</c>; a fill webhook (or follow-up poll) will later move it
/// to <c>filled</c> once the matching engine fills it.
/// </param>
/// <param name="OrderId">CLOB-assigned order id when <c>Success</c> is true.</param>
/// <param name="FailureReason">Human-readable reason on failure (logged + persisted).</param>
public sealed record OrderPlacementResult(
    bool Success,
    string? OrderId,
    string? FailureReason);

/// <summary>
/// Inputs needed to place a single CLOB order. Mirrors the CopyTradeExecution
/// row that produced it but omits IDs the CLOB doesn't care about.
/// </summary>
public sealed record OrderRequest(
    string Asset,           // CLOB token id
    string Side,            // "BUY" or "SELL"
    decimal Price,          // 0..1
    decimal SizeShares);
