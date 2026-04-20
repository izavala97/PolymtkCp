using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PolymtkCp.Models;
using PolymtkCp.Services.Watcher;
using Supabase.Postgrest;

namespace PolymtkCp.Services.Executor;

/// <summary>
/// Background service that picks up real-mode CopyTradeExecution rows the
/// watcher has marked <c>pending</c>, looks up the Follower's encrypted
/// Polymarket credentials, asks the <see cref="PolymarketClobClient"/> to
/// place the order, and updates the row to <c>submitted</c> (with the
/// CLOB order id) or <c>failed</c> (with a human-readable reason).
///
/// <para>Like the watcher, this runs only when the Supabase service-role
/// key is configured — it needs to read every Follower's pending rows and
/// write back execution outcomes across all rows (RLS-bypass territory).</para>
///
/// <para>The CLOB EIP-712 signing implementation is still TODO (see
/// <see cref="PolymarketClobClient"/>); until that lands, every pending
/// row gracefully transitions to <c>failed</c> with a clear reason rather
/// than being silently dropped.</para>
/// </summary>
public sealed class OrderExecutor : BackgroundService
{
    private readonly WatcherSupabase _supa;
    private readonly ExecutorSecretReader _secrets;
    private readonly PolymarketClobClient _clob;
    private readonly ExecutorOptions _options;
    private readonly ILogger<OrderExecutor> _logger;

    public OrderExecutor(
        WatcherSupabase supa,
        ExecutorSecretReader secrets,
        PolymarketClobClient clob,
        IOptions<ExecutorOptions> options,
        ILogger<OrderExecutor> logger)
    {
        _supa = supa;
        _secrets = secrets;
        _clob = clob;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OrderExecutor started. Polling every {Interval}s, batch size {BatchSize}.",
            _options.PollInterval.TotalSeconds, _options.BatchSize);

        try { await TickAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _logger.LogError(ex, "Initial executor tick failed."); }

        using var timer = new PeriodicTimer(_options.PollInterval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Executor tick failed."); }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var client = _supa.Client;

        // The (mode='real', status='pending') subset is indexed by created_at
        // (see migration 20260420190000_executor_pending_index). Pull oldest first
        // so users see fair FIFO order under load.
        var pendingResp = await client
            .From<CopyTradeExecution>()
            .Filter("mode", Constants.Operator.Equals, "real")
            .Filter("status", Constants.Operator.Equals, "pending")
            .Order("created_at", Constants.Ordering.Ascending)
            .Limit(_options.BatchSize)
            .Get(ct);

        var pending = pendingResp.Models;
        if (pending.Count == 0) return;

        _logger.LogInformation("Executor: processing {Count} pending real-mode rows.", pending.Count);

        // Cache decrypted credentials per follower for the duration of this tick;
        // an active trader may have many pending rows in one batch.
        var credCache = new Dictionary<Guid, Secrets.PolymarketCredentials?>();

        foreach (var row in pending)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                if (!credCache.TryGetValue(row.FollowerId, out var creds))
                {
                    creds = await _secrets.GetActiveAsync(row.FollowerId, ct);
                    credCache[row.FollowerId] = creds;
                }

                if (creds is null)
                {
                    await MarkFailedAsync(row,
                        "No active Polymarket credentials found for this Follower.", ct);
                    continue;
                }
                if (string.IsNullOrEmpty(creds.PrivateKey))
                {
                    await MarkFailedAsync(row,
                        "Wallet private key missing from saved credentials \u2014 required to sign CLOB orders.", ct);
                    continue;
                }

                var result = await _clob.PlaceOrderAsync(
                    new OrderRequest(row.Asset, row.Side, row.Price, row.SizeShares),
                    creds, ct);

                if (result.Success)
                    await MarkSubmittedAsync(row, result.OrderId, ct);
                else
                    await MarkFailedAsync(row, result.FailureReason ?? "Unknown CLOB failure.", ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Executor: row {RowId} crashed.", row.Id);
                try { await MarkFailedAsync(row, $"Executor exception: {ex.GetType().Name}", ct); }
                catch { /* best effort — already logged */ }
            }
        }
    }

    private async Task MarkSubmittedAsync(CopyTradeExecution row, string? orderId, CancellationToken ct)
    {
        var client = _supa.Client;
        var nowUtc = DateTime.UtcNow;
        await client.From<CopyTradeExecution>()
            .Where(r => r.Id == row.Id)
            .Set(r => r.Status, "submitted")
            .Set(r => r.ExecutedAt!, nowUtc)
            .Set(r => r.Reason!, orderId is null ? null : $"CLOB order id: {orderId}")
            .Update();
        _logger.LogInformation(
            "Executor: row {RowId} submitted to CLOB (orderId={OrderId}).",
            row.Id, orderId);
    }

    private async Task MarkFailedAsync(CopyTradeExecution row, string reason, CancellationToken ct)
    {
        var client = _supa.Client;
        await client.From<CopyTradeExecution>()
            .Where(r => r.Id == row.Id)
            .Set(r => r.Status, "failed")
            .Set(r => r.Reason!, Truncate(reason, 500))
            .Update();
        _logger.LogWarning(
            "Executor: row {RowId} failed for follower {FollowerId}: {Reason}",
            row.Id, row.FollowerId, reason);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "\u2026";
}
