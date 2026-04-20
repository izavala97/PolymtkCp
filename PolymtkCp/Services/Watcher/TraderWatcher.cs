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

        foreach (var t in fresh)
        {
            var (status, reason, sizeUsdc, sizeShares) =
                Decide(plan, t, todayCount, todayMoney);

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
    private static (string Status, string? Reason, decimal SizeUsdc, decimal SizeShares)
        Decide(CopyPlan plan, PolymarketActivity t, int todayCount, decimal todayMoney)
    {
        // Sizing
        decimal sizeUsdc = plan.SizingMode switch
        {
            "fixed" => plan.FixedAmountUsd ?? 0m,
            "percent" => Math.Round(t.UsdcSize * (plan.PercentOfNotional ?? 0m) / 100m, 4),
            _ => 0m,
        };

        // Validity gates → skip with a human-readable reason
        if (sizeUsdc <= 0)
            return ("skipped", "Computed size is zero", sizeUsdc, 0m);
        if (string.IsNullOrEmpty(t.Asset))
            return ("skipped", "Source trade missing asset id", sizeUsdc, 0m);
        if (plan.DailyTradeOperationsLimit is int opMax && todayCount >= opMax)
            return ("skipped", $"Daily ops limit ({opMax}) reached", sizeUsdc, 0m);
        if (plan.DailyTradeMoneyLimit is decimal moneyMax && todayMoney + sizeUsdc > moneyMax)
            return ("skipped", $"Daily money limit (${moneyMax:N2}) would be exceeded", sizeUsdc, 0m);

        decimal sizeShares = t.Price > 0
            ? Math.Round(sizeUsdc / t.Price, 6)
            : 0m;
        return ("simulated", null, sizeUsdc, sizeShares);
    }
}
