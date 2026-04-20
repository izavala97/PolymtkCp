using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PolymtkCp.Models;
using PolymtkCp.Services.Polymarket;

namespace PolymtkCp.Pages.Traders;

public class LeaderboardModel : PageModel
{
    private readonly Supabase.Client _supabase;
    private readonly TraderStatsService _stats;
    private readonly ILogger<LeaderboardModel> _logger;

    // How many traders we'll score. Bounded to keep the page fast and
    // respectful of Polymarket's public API.
    private const int MaxTradersToScore = 50;
    // Parallelism cap on the API fan-out.
    private const int Concurrency = 6;

    public LeaderboardModel(
        Supabase.Client supabase,
        TraderStatsService stats,
        ILogger<LeaderboardModel> logger)
    {
        _supabase = supabase;
        _stats = stats;
        _logger = logger;
    }

    public sealed record Row(
        Trader Trader,
        TraderStatsService.TraderStats Stats,
        bool AlreadyCopied);

    public List<Row> Rows { get; private set; } = [];
    public string? ErrorMessage { get; set; }

    [BindProperty(SupportsGet = true, Name = "min")]
    public int MinSample { get; set; } = 10;

    [BindProperty(SupportsGet = true, Name = "active")]
    public bool ActiveOnly { get; set; } = true;

    [BindProperty(SupportsGet = true, Name = "sort")]
    public string Sort { get; set; } = "winrate";

    public async Task OnGetAsync()
    {
        try
        {
            var followerId = GetFollowerId();

            var tradersResp = await _supabase.From<Trader>().Get();
            var traders = tradersResp.Models;

            // Mark which traders this Follower is already copying.
            var plansResp = await _supabase
                .From<CopyPlan>()
                .Where(p => p.FollowerId == followerId)
                .Get();
            var copiedTraderIds = plansResp.Models
                .Select(p => p.TraderId)
                .ToHashSet();

            // Bound the fan-out so we don't melt the public API.
            var subset = traders.Take(MaxTradersToScore).ToList();

            using var sem = new SemaphoreSlim(Concurrency);
            var ct = HttpContext.RequestAborted;
            var tasks = subset.Select(async t =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var s = await _stats.GetAsync(t.WalletAddress, ct);
                    return new Row(t, s, copiedTraderIds.Contains(t.Id));
                }
                finally
                {
                    sem.Release();
                }
            });

            var rows = (await Task.WhenAll(tasks)).ToList();

            // Apply filters
            var filtered = rows
                .Where(r => r.Stats.DataAvailable)
                .Where(r => r.Stats.Sample >= MinSample)
                .Where(r => !ActiveOnly || r.Stats.IsActive)
                .ToList();

            // Sort
            filtered = Sort switch
            {
                "net" => filtered.OrderByDescending(r => r.Stats.NetResolvedUsdc).ToList(),
                "volume" => filtered.OrderByDescending(r => r.Stats.TradeCount30d).ToList(),
                "wins" => filtered.OrderByDescending(r => r.Stats.WinCount).ToList(),
                _ => filtered
                    .OrderByDescending(r => r.Stats.WinRate ?? 0m)
                    .ThenByDescending(r => r.Stats.Sample)
                    .ToList(),
            };

            Rows = filtered;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Leaderboard render failed.");
            ErrorMessage = "Could not build the leaderboard. Please try again.";
        }
    }

    private Guid GetFollowerId()
    {
        var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id)
            ? id
            : throw new InvalidOperationException("Authenticated user has no Supabase id claim.");
    }
}
