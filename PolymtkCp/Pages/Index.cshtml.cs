using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using PolymtkCp.Models;
using PolymtkCp.Services.Polymarket;

namespace PolymtkCp.Pages;

public class IndexModel : PageModel
{
    /// <summary>Update this to your repo URL — surfaced as the GitHub CTA.</summary>
    public const string GitHubUrl = "https://github.com/maizorin97/PolymtkCp";

    private static readonly TimeSpan BalanceCacheTtl = TimeSpan.FromSeconds(60);

    private readonly Supabase.Client _supabase;
    private readonly PolymarketClient _polymarket;
    private readonly PolygonUsdcClient _polygon;
    private readonly IMemoryCache _cache;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        Supabase.Client supabase,
        PolymarketClient polymarket,
        PolygonUsdcClient polygon,
        IMemoryCache cache,
        ILogger<IndexModel> logger)
    {
        _supabase = supabase;
        _polymarket = polymarket;
        _polygon = polygon;
        _cache = cache;
        _logger = logger;
    }

    public bool IsAuthenticated => User.Identity?.IsAuthenticated == true;

    public string? WalletAddress { get; private set; }
    public decimal? Cash { get; private set; }
    public decimal? Positions { get; private set; }
    public decimal? Portfolio => Cash is null && Positions is null ? null : (Cash ?? 0m) + (Positions ?? 0m);
    public bool BalanceFailed { get; private set; }

    public IReadOnlyList<CopyTradeExecution> RecentExecutions { get; private set; } = [];

    /// <summary>Date labels for the cumulative-deployed-$ chart x-axis (last 30d).</summary>
    public IReadOnlyList<string> ChartLabels { get; private set; } = [];
    /// <summary>Cumulative simulated $ deployed per day, aligned with ChartLabels.</summary>
    public IReadOnlyList<decimal> ChartCumulativeUsdc { get; private set; } = [];

    public int TotalSimulatedTrades { get; private set; }
    public decimal TotalSimulatedUsdc { get; private set; }

    public async Task OnGetAsync()
    {
        if (!IsAuthenticated) return;

        var followerId = GetFollowerIdOrNull();
        if (followerId is null) return;

        await Task.WhenAll(LoadBalanceAsync(followerId.Value), LoadActivityAsync(followerId.Value));
    }

    private async Task LoadBalanceAsync(Guid followerId)
    {
        try
        {
            var profile = await _supabase
                .From<FollowerProfile>()
                .Where(p => p.FollowerId == followerId)
                .Single();
            WalletAddress = profile?.PolymarketWalletAddress;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Index: failed to load follower profile.");
            BalanceFailed = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(WalletAddress)) return;

        var cacheKey = $"pmkt:bal:{WalletAddress}";
        if (_cache.TryGetValue(cacheKey, out (decimal? positions, decimal? cash) cached))
        {
            Positions = cached.positions;
            Cash = cached.cash;
            return;
        }

        var pTask = SafeAsync(() => _polymarket.GetPortfolioValueAsync(WalletAddress!), "positions");
        var cTask = SafeAsync(() => _polygon.GetCashAsync(WalletAddress!), "cash");
        await Task.WhenAll(pTask, cTask);
        Positions = pTask.Result;
        Cash = cTask.Result;
        BalanceFailed = Positions is null && Cash is null;
        if (!BalanceFailed)
            _cache.Set(cacheKey, (Positions, Cash), BalanceCacheTtl);
    }

    private async Task LoadActivityAsync(Guid followerId)
    {
        try
        {
            // Last 30 days — what copy-trades the watcher has emitted for this Follower.
            var since = DateTime.UtcNow.AddDays(-30);
            var resp = await _supabase
                .From<CopyTradeExecution>()
                .Where(e => e.FollowerId == followerId && e.CreatedAt >= since)
                .Order(e => e.CreatedAt, Supabase.Postgrest.Constants.Ordering.Descending)
                .Limit(500)
                .Get();
            var rows = resp.Models;

            RecentExecutions = rows.Take(10).ToList();
            var simulated = rows.Where(r => r.Status == "simulated").ToList();
            TotalSimulatedTrades = simulated.Count;
            TotalSimulatedUsdc = simulated.Sum(r => r.SizeUsdc);

            // Build a per-day cumulative deployed-$ series for the chart.
            var today = DateTime.UtcNow.Date;
            var labels = new List<string>(30);
            var values = new List<decimal>(30);
            var perDay = simulated
                .GroupBy(r => r.CreatedAt.Date)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.SizeUsdc));

            decimal running = 0m;
            for (var i = 29; i >= 0; i--)
            {
                var day = today.AddDays(-i);
                labels.Add(day.ToString("MM-dd"));
                if (perDay.TryGetValue(day, out var dayTotal)) running += dayTotal;
                values.Add(running);
            }
            ChartLabels = labels;
            ChartCumulativeUsdc = values;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Index: failed to load copy-trade executions.");
        }
    }

    private Guid? GetFollowerIdOrNull()
    {
        var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private async Task<decimal?> SafeAsync(Func<Task<decimal>> call, string label)
    {
        try { return await call(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Index balance: {Label} call failed.", label);
            return null;
        }
    }
}
