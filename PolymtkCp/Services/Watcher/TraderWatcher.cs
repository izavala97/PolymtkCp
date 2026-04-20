using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PolymtkCp.Models;
using PolymtkCp.Services.Polymarket;
using Supabase.Postgrest;

namespace PolymtkCp.Services.Watcher;

/// <summary>
/// Polls every active CopyPlan's Trader on Polymarket's public Data API,
/// detects new trades, scales them per the plan's sizing/limits, and writes
/// rows to <c>copy_trade_executions</c>. Phase 1: every emitted row has
/// <c>mode = "paper"</c> and either <c>status = "simulated"</c> or
/// <c>status = "skipped"</c> (with a reason). The phase-2 executor will
/// later pick up real-mode plans and submit orders to the CLOB.
/// </summary>
public sealed class TraderWatcher : BackgroundService
{
    private readonly WatcherSupabase _supa;
    private readonly PolymarketClient _polymarket;
    private readonly WatcherOptions _options;
    private readonly ILogger<TraderWatcher> _logger;

    public TraderWatcher(
        WatcherSupabase supa,
        PolymarketClient polymarket,
        IOptions<WatcherOptions> options,
        ILogger<TraderWatcher> logger)
    {
        _supa = supa;
        _polymarket = polymarket;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TraderWatcher started. Polling every {Interval}s.",
            _options.PollInterval.TotalSeconds);

        // First tick immediately, then on the configured cadence.
        try { await TickAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _logger.LogError(ex, "Initial watcher tick failed."); }

        using var timer = new PeriodicTimer(_options.PollInterval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Watcher tick failed."); }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var client = _supa.Client;

        // Active, non-expired plans across all Followers.
        var plansResp = await client
            .From<CopyPlan>()
            .Where(p => p.IsActive == true)
            .Get(ct);
        var plans = plansResp.Models
            .Where(p => p.ExpiresAt is null || p.ExpiresAt > nowUtc)
            .ToList();
        if (plans.Count == 0)
            return;

        // Resolve traders for the active plan set (one IN query, not N).
        var traderIds = plans.Select(p => p.TraderId).Distinct().Select(g => g.ToString()).ToList();
        var tradersResp = await client
            .From<Trader>()
            .Filter("id", Constants.Operator.In, traderIds)
            .Get(ct);
        var traderById = tradersResp.Models.ToDictionary(t => t.Id);

        // Group plans by trader so we hit /activity once per unique trader.
        foreach (var grp in plans.GroupBy(p => p.TraderId))
        {
            if (!traderById.TryGetValue(grp.Key, out var trader))
                continue;

            IReadOnlyList<PolymarketActivity> trades;
            try
            {
                var activity = await _polymarket.GetActivityAsync(
                    trader.WalletAddress, _options.ActivityPageSize, ct);
                trades = activity
                    .Where(a => a.Type == "TRADE" && !string.IsNullOrEmpty(a.TransactionHash))
                    .OrderBy(a => a.TimestampUnix)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to fetch activity for trader {Wallet}.", trader.WalletAddress);
                continue;
            }

            if (trades.Count == 0)
                continue;

            foreach (var plan in grp)
                await ProcessPlanAsync(plan, trades, nowUtc, ct);
        }
    }

    private async Task ProcessPlanAsync(
        CopyPlan plan,
        IReadOnlyList<PolymarketActivity> trades,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var client = _supa.Client;

        // Don't replay history: only consider trades on/after the plan's creation.
        var planCreated = DateTime.SpecifyKind(plan.CreatedAt, DateTimeKind.Utc);
        var candidates = trades
            .Where(t => t.Timestamp.UtcDateTime >= planCreated)
            .ToList();
        if (candidates.Count == 0)
            return;

        // Filter to trades not yet recorded for this plan.
        var hashes = candidates.Select(t => t.TransactionHash!).ToList();
        var existingResp = await client
            .From<CopyTradeExecution>()
            .Where(e => e.CopyPlanId == plan.Id)
            .Filter("source_activity_hash", Constants.Operator.In, hashes)
            .Get(ct);
        var existingHashes = existingResp.Models
            .Select(e => e.SourceActivityHash)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fresh = candidates
            .Where(t => !existingHashes.Contains(t.TransactionHash!))
            .ToList();
        if (fresh.Count == 0)
            return;

        // Today's window (UTC) for daily limit accounting.
        var dayStart = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc);
        var todayResp = await client
            .From<CopyTradeExecution>()
            .Where(e => e.CopyPlanId == plan.Id && e.CreatedAt >= dayStart)
            .Get(ct);
        // Skipped rows shouldn't count toward limits.
        var todayApplied = todayResp.Models.Where(r => r.Status != "skipped").ToList();
        var todayCount = todayApplied.Count;
        var todayMoney = todayApplied.Sum(r => r.SizeUsdc);

        // Grouping: if plan.GroupSimilarOps = N, collapse runs of adjacent same-(asset, side)
        // fills into chunks of N. Only the first fill of each chunk is "eligible" to emit a
        // simulated copy-trade; the rest are forced to skipped with reason='grouped'.
        // The leader's copied size is multiplied by its chunk size so collapsing preserves
        // total volume (e.g. N=2 over 4 fills emits 2 copies of 2x base size, not 2 copies
        // of 1x — otherwise accumulating in small chunks and exiting in one big order would
        // asymmetrically under-sell the follower's position).
        // We still insert one row per source hash to preserve 1:1 dedup across ticks.
        var groupedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var leaderMultiplier = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // For chunk leaders: aggregate source size across the chunk, so the persisted row is
        // auto-consistent — its copy size and its recorded source size represent the same
        // chunk. Followers (skipped/grouped) keep their individual fill data for audit.
        var leaderChunkAggregate =
            new Dictionary<string, (decimal Shares, decimal Usdc, decimal Vwap)>(StringComparer.OrdinalIgnoreCase);
        if (plan.GroupSimilarOps is int groupN && groupN >= 2)
        {
            var runStart = 0;
            for (var i = 1; i <= fresh.Count; i++)
            {
                var endOfRun = i == fresh.Count
                    || fresh[i].Asset != fresh[runStart].Asset
                    || fresh[i].Side != fresh[runStart].Side;
                if (!endOfRun) continue;

                // fresh[runStart..i) is one run of same (asset, side); split into chunks of N.
                for (var chunkStart = runStart; chunkStart < i; chunkStart += groupN)
                {
                    var chunkEnd = Math.Min(chunkStart + groupN, i);
                    var chunkSize = chunkEnd - chunkStart;
                    var leaderHash = fresh[chunkStart].TransactionHash!;
                    leaderMultiplier[leaderHash] = chunkSize;

                    decimal chunkShares = 0m, chunkUsdc = 0m;
                    for (var j = chunkStart; j < chunkEnd; j++)
                    {
                        chunkShares += fresh[j].Size;
                        chunkUsdc += fresh[j].UsdcSize;
                        if (j > chunkStart)
                            groupedHashes.Add(fresh[j].TransactionHash!);
                    }
                    var chunkVwap = chunkShares > 0 ? Math.Round(chunkUsdc / chunkShares, 6) : 0m;
                    leaderChunkAggregate[leaderHash] = (chunkShares, chunkUsdc, chunkVwap);
                }
                runStart = i;
            }
        }

        foreach (var t in fresh)
        {
            string status;
            string? reason;
            decimal sizeUsdc;
            decimal sizeShares;

            if (groupedHashes.Contains(t.TransactionHash!))
            {
                // Forced skip: this fill is absorbed into its chunk's leader.
                status = "skipped";
                reason = $"grouped (N={plan.GroupSimilarOps})";
                sizeUsdc = 0m;
                sizeShares = 0m;
            }
            else
            {
                var mult = leaderMultiplier.TryGetValue(t.TransactionHash!, out var m) ? m : 1;
                (status, reason, sizeUsdc, sizeShares) =
                    Decide(plan, t, todayCount, todayMoney, mult);
            }

            // Source amounts: for chunk leaders we store the chunk's aggregate so the
            // row is self-describing; grouped followers keep their individual fill data.
            decimal srcPrice = t.Price;
            decimal srcShares = t.Size;
            decimal srcUsdc = t.UsdcSize;
            if (leaderChunkAggregate.TryGetValue(t.TransactionHash!, out var agg))
            {
                srcShares = agg.Shares;
                srcUsdc = agg.Usdc;
                srcPrice = agg.Vwap;
            }

            var row = new CopyTradeExecution
            {
                CopyPlanId = plan.Id,
                FollowerId = plan.FollowerId,
                Mode = "paper",                   // phase 1: never live
                Status = status,
                SourceActivityHash = t.TransactionHash!,
                SourceTimestamp = t.Timestamp.UtcDateTime,
                Asset = t.Asset ?? string.Empty,
                ConditionId = t.ConditionId,
                Side = t.Side ?? "BUY",
                Price = t.Price,
                SizeShares = sizeShares,
                SizeUsdc = sizeUsdc,
                SourcePrice = srcPrice,
                SourceSizeShares = srcShares,
                SourceSizeUsdc = srcUsdc,
                EventTitle = t.Title,
                Outcome = t.Outcome,
                Slug = t.Slug,
                Reason = reason,
            };

            try
            {
                await client.From<CopyTradeExecution>().Insert(row);
                if (status != "skipped")
                {
                    todayCount++;
                    todayMoney += sizeUsdc;
                }
                _logger.LogInformation(
                    "Watcher: plan {PlanId} {Status} {Side} ${Size:N2} on \"{Event}\".",
                    plan.Id, status, row.Side, sizeUsdc, row.EventTitle ?? row.Slug ?? "?");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Watcher: failed to insert execution for plan {PlanId}, hash {Hash}.",
                    plan.Id, t.TransactionHash);
            }
        }
    }

    /// <summary>Plan + trade -> (status, reason, sizeUsdc, sizeShares).</summary>
    /// <param name="sizeMultiplier">
    /// When &gt; 1, the emitted size is scaled up to represent this many adjacent
    /// same-(asset, side) fills collapsed into one copy-trade (see grouping logic).
    /// </param>
    private static (string Status, string? Reason, decimal SizeUsdc, decimal SizeShares)
        Decide(CopyPlan plan, PolymarketActivity t, int todayCount, decimal todayMoney,
            int sizeMultiplier = 1)
    {
        // Sizing
        decimal baseSize = plan.SizingMode switch
        {
            "fixed" => plan.FixedAmountUsd ?? 0m,
            "percent" => Math.Round(t.UsdcSize * (plan.PercentOfNotional ?? 0m) / 100m, 4),
            _ => 0m,
        };
        decimal sizeUsdc = baseSize * sizeMultiplier;

        // Validity gates → skip with a human-readable reason
        if (sizeUsdc <= 0)
            return ("skipped", "Computed size is zero", sizeUsdc, 0m);
        if (string.IsNullOrEmpty(t.Asset))
            return ("skipped", "Source trade missing asset id", sizeUsdc, 0m);
        if (plan.DailyTradeOperationsLimit is int opMax && todayCount >= opMax)
            return ("skipped", $"Daily ops limit ({opMax}) reached", sizeUsdc, 0m);

        // Daily money limit: trim to what's left instead of skipping the whole order,
        // so the Follower still mirrors direction even when the budget is almost spent.
        // Skip only if nothing remains. Same pattern applies in phase 2 when we cap by
        // real balance (cash for BUY, shares-held for SELL).
        string? trimReason = null;
        if (plan.DailyTradeMoneyLimit is decimal moneyMax)
        {
            var remaining = moneyMax - todayMoney;
            if (remaining <= 0)
                return ("skipped", $"Daily money limit (${moneyMax:N2}) reached", sizeUsdc, 0m);
            if (sizeUsdc > remaining)
            {
                trimReason = $"trimmed to daily money limit (${moneyMax:N2})";
                sizeUsdc = Math.Round(remaining, 4);
            }
        }

        decimal sizeShares = t.Price > 0
            ? Math.Round(sizeUsdc / t.Price, 6)
            : 0m;
        return ("simulated", trimReason, sizeUsdc, sizeShares);
    }
}
