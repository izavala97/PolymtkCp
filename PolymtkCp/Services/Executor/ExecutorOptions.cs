namespace PolymtkCp.Services.Executor;

/// <summary>
/// Tunables for the OrderExecutor background service. Bind from the
/// <c>"Executor"</c> section in appsettings.
/// </summary>
public sealed class ExecutorOptions
{
    public const string SectionName = "Executor";

    /// <summary>
    /// How often the executor polls for pending real-mode rows. Should be
    /// equal to or smaller than the watcher's PollInterval — otherwise
    /// pending rows pile up between executor ticks.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Cap on rows processed per tick to avoid unbounded loops if the
    /// pending queue grows. Excess rows are picked up on the next tick.
    /// </summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>
    /// If false, the hosted service is not registered. Useful for tests
    /// or to disable the executor in environments where order placement
    /// must be off (e.g. a staging deploy connected to prod CLOB).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Polymarket CLOB base URL.
    /// </summary>
    public string ClobBaseUrl { get; set; } = "https://clob.polymarket.com";

    /// <summary>
    /// Polygon chain id used in the EIP-712 domain separator for order signing.
    /// 137 = Polygon mainnet (Polymarket's home network).
    /// </summary>
    public int ChainId { get; set; } = 137;
}
