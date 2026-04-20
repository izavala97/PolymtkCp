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
    /// <summary>Cumulative paper (simulated) $ deployed per day, aligned with ChartLabels.</summary>
    public IReadOnlyList<decimal> ChartPaperCumulativeUsdc { get; private set; } = [];
    /// <summary>Cumulative real (submitted/filled) $ deployed per day, aligned with ChartLabels.</summary>
    public IReadOnlyList<decimal> ChartRealCumulativeUsdc { get; private set; } = [];

    public int TotalPaperTrades { get; private set; }
    public decimal TotalPaperUsdc { get; private set; }
    public int TotalRealTrades { get; private set; }
    public decimal TotalRealUsdc { get; private set; }
    public bool HasRealTrades => TotalRealTrades > 0;

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

            // "paper" = mode paper + status simulated (phase 1 default).
            // "real"  = mode real + status that reflects capital deployed (submitted / filled).
            var paper = rows.Where(r => r.Mode == "paper" && r.Status == "simulated").ToList();
            var real = rows.Where(r => r.Mode == "real"
                                       && (r.Status == "submitted" || r.Status == "filled")).ToList();

            TotalPaperTrades = paper.Count;
            TotalPaperUsdc = paper.Sum(r => r.SizeUsdc);
            TotalRealTrades = real.Count;
            TotalRealUsdc = real.Sum(r => r.SizeUsdc);

            // Build per-day cumulative deployed-$ series for each mode.
            var today = DateTime.UtcNow.Date;
            var labels = new List<string>(30);
            var paperValues = new List<decimal>(30);
            var realValues = new List<decimal>(30);
            var paperPerDay = paper.GroupBy(r => r.CreatedAt.Date)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.SizeUsdc));
            var realPerDay = real.GroupBy(r => r.CreatedAt.Date)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.SizeUsdc));

            decimal paperRunning = 0m, realRunning = 0m;
            for (var i = 29; i >= 0; i--)
            {
                var day = today.AddDays(-i);
                labels.Add(day.ToString("MM-dd"));
                if (paperPerDay.TryGetValue(day, out var pDay)) paperRunning += pDay;
                if (realPerDay.TryGetValue(day, out var rDay)) realRunning += rDay;
                paperValues.Add(paperRunning);
                realValues.Add(realRunning);
            }
            ChartLabels = labels;
            ChartPaperCumulativeUsdc = paperValues;
            ChartRealCumulativeUsdc = realValues;
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
