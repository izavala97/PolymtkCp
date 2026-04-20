using Microsoft.Extensions.Caching.Memory;

namespace PolymtkCp.Services.Polymarket;

/// <summary>
/// Computes "skill, not luck" stats for a Polymarket wallet from the public
/// activity + positions endpoints, and caches the result for ~15 min so the
/// leaderboard page doesn't hammer the API on every render.
///
/// Wins  = distinct conditionIds with at least one REDEEM event in /activity
///         (REDEEM = trader cashed out winning shares at $1 each).
/// Losses = open positions with currentValue == 0 (resolved against them and
///         left unredeemed — Polymarket leaves them in /positions forever).
///
/// This is an approximation, not perfect realized PnL: it doesn't credit
/// partial sells before resolution, and it can't see positions the trader
/// fully exited at a loss before resolution. But it is far more honest than
/// "USDC profit" which is dominated by one lucky bet.
/// </summary>
public sealed class TraderStatsService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private readonly PolymarketClient _polymarket;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TraderStatsService> _logger;

    public TraderStatsService(
        PolymarketClient polymarket,
        IMemoryCache cache,
        ILogger<TraderStatsService> logger)
    {
        _polymarket = polymarket;
        _cache = cache;
        _logger = logger;
    }

    public sealed record TraderStats(
        string WalletAddress,
        int WinCount,
        int LossCount,
        decimal RedeemedUsdc,
        decimal LostUsdc,
        int TradeCount30d,
        DateTimeOffset? LastTradeAt,
        bool DataAvailable,
        string? Error)
    {
        public int Sample => WinCount + LossCount;
        public decimal? WinRate => Sample == 0 ? null : (decimal)WinCount / Sample;
        public decimal NetResolvedUsdc => RedeemedUsdc - LostUsdc;
        public bool IsActive => LastTradeAt is { } t && (DateTimeOffset.UtcNow - t).TotalDays <= 7;
    }

    public Task<TraderStats> GetAsync(string walletAddress, CancellationToken ct = default)
    {
        var key = $"traderstats:{walletAddress.ToLowerInvariant()}";
        return _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await ComputeAsync(walletAddress, ct);
        })!;
    }

    private async Task<TraderStats> ComputeAsync(string wallet, CancellationToken ct)
    {
        try
        {
            // Pull a deep slice of activity. 1000 covers months for most wallets.
            var positionsTask = _polymarket.GetPositionsAsync(wallet, ct);
            var activityTask = _polymarket.GetActivityAsync(wallet, limit: 1000, ct);
            await Task.WhenAll(positionsTask, activityTask);

            var positions = await positionsTask;
            var activity = await activityTask;

            // Wins: distinct conditionIds with at least one REDEEM.
            var redeemEvents = activity.Where(a => a.Type == "REDEEM").ToList();
            var winConditions = redeemEvents
                .Where(a => !string.IsNullOrEmpty(a.ConditionId))
                .Select(a => a.ConditionId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Losses: positions resolved at $0 that the trader never bothered to
            // redeem (because they're worthless). Exclude tiny dust.
            var lostPositions = positions
                .Where(p => p.CurrentValue == 0m && p.InitialValue >= 1m)
                .ToList();

            // 30-day trade volume for the recency badge / leaderboard filtering.
            var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
            var trades = activity.Where(a => a.Type == "TRADE").ToList();
            var trades30d = trades.Where(a => a.Timestamp >= cutoff).ToList();
            var lastTrade = trades.Count == 0 ? (DateTimeOffset?)null : trades.Max(a => a.Timestamp);

            return new TraderStats(
                WalletAddress: wallet,
                WinCount: winConditions.Count,
                LossCount: lostPositions.Count,
                RedeemedUsdc: redeemEvents.Sum(a => a.UsdcSize),
                LostUsdc: lostPositions.Sum(p => p.InitialValue),
                TradeCount30d: trades30d.Count,
                LastTradeAt: lastTrade,
                DataAvailable: true,
                Error: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TraderStats computation failed for {Wallet}.", wallet);
            return new TraderStats(
                WalletAddress: wallet,
                WinCount: 0,
                LossCount: 0,
                RedeemedUsdc: 0m,
                LostUsdc: 0m,
                TradeCount30d: 0,
                LastTradeAt: null,
                DataAvailable: false,
                Error: ex.Message);
        }
    }
}
