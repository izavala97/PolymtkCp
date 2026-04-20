using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PolymtkCp.Models;
using PolymtkCp.Services.Polymarket;

namespace PolymtkCp.Pages.Traders;

public class DetailModel : PageModel
{
    private readonly Supabase.Client _supabase;
    private readonly PolymarketClient _polymarket;
    private readonly ILogger<DetailModel> _logger;

    public DetailModel(
        Supabase.Client supabase,
        PolymarketClient polymarket,
        ILogger<DetailModel> logger)
    {
        _supabase = supabase;
        _polymarket = polymarket;
        _logger = logger;
    }

    public CopyPlan? Plan { get; private set; }
    public Trader? Trader { get; private set; }
    public IReadOnlyList<PolymarketPosition> Positions { get; private set; } = [];
    public IReadOnlyList<PolymarketActivity> Activity { get; private set; } = [];
    public IReadOnlyList<CopyTradeExecution> Executions { get; private set; } = [];
    public ActivityStats Stats30d { get; private set; } = new();
    public string? ErrorMessage { get; set; }

    public const int PageSize = 20;

    [BindProperty(SupportsGet = true, Name = "pp")] public int PositionsPage { get; set; } = 1;
    [BindProperty(SupportsGet = true, Name = "pa")] public int ActivityPage { get; set; } = 1;
    [BindProperty(SupportsGet = true, Name = "pe")] public int ExecutionsPage { get; set; } = 1;

    public int PositionsTotal { get; private set; }
    public int ActivityTotal { get; private set; }
    public int ExecutionsTotal { get; private set; }

    public sealed class ActivityStats
    {
        public int TradeCount { get; init; }
        public decimal BuyUsdc { get; init; }
        public decimal SellUsdc { get; init; }
        public decimal RedeemUsdc { get; init; }
        public decimal NetCashFlow => SellUsdc + RedeemUsdc - BuyUsdc;
        public DateTimeOffset? OldestTradeAt { get; init; }
        public DateTimeOffset? NewestTradeAt { get; init; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var followerId = GetFollowerId();

        Plan = await _supabase
            .From<CopyPlan>()
            .Where(p => p.Id == id && p.FollowerId == followerId)
            .Single();

        if (Plan is null)
            return NotFound();

        Trader = await _supabase
            .From<Trader>()
            .Where(t => t.Id == Plan.TraderId)
            .Single();

        if (Trader is null)
            return NotFound();

        try
        {
            var executionsResp = await _supabase
                .From<CopyTradeExecution>()
                .Where(e => e.CopyPlanId == Plan.Id)
                .Order(e => e.CreatedAt, Supabase.Postgrest.Constants.Ordering.Descending)
                .Limit(500)
                .Get();
            var allExecs = executionsResp.Models;
            ExecutionsTotal = allExecs.Count;
            if (ExecutionsPage < 1) ExecutionsPage = 1;
            Executions = allExecs.Skip((ExecutionsPage - 1) * PageSize).Take(PageSize).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load executions for plan {PlanId}.", Plan.Id);
        }

        try
        {
            var positionsTask = _polymarket.GetPositionsAsync(Trader.WalletAddress);
            var activityTask = _polymarket.GetActivityAsync(Trader.WalletAddress, limit: 500);
            await Task.WhenAll(positionsTask, activityTask);

            Positions = (await positionsTask)
                .OrderByDescending(p => p.CurrentValue)
                .ToList();

            var allActivity = (await activityTask)
                .OrderByDescending(a => a.TimestampUnix)
                .ToList();

            // Trade rows for the activity table (paginated below)
            var allTradeActivity = allActivity
                .Where(a => a.Type == "TRADE")
                .ToList();

            PositionsTotal = Positions.Count;
            ActivityTotal = allTradeActivity.Count;

            if (PositionsPage < 1) PositionsPage = 1;
            if (ActivityPage < 1) ActivityPage = 1;

            Positions = Positions.Skip((PositionsPage - 1) * PageSize).Take(PageSize).ToList();
            Activity = allTradeActivity.Skip((ActivityPage - 1) * PageSize).Take(PageSize).ToList();

            // 30-day stats: include both TRADE and REDEEM cash flows
            var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
            var window = allActivity.Where(a => a.Timestamp >= cutoff).ToList();
            var trades = window.Where(a => a.Type == "TRADE").ToList();

            Stats30d = new ActivityStats
            {
                TradeCount = trades.Count,
                BuyUsdc = trades.Where(a => a.Side == "BUY").Sum(a => a.UsdcSize),
                SellUsdc = trades.Where(a => a.Side == "SELL").Sum(a => a.UsdcSize),
                RedeemUsdc = window.Where(a => a.Type == "REDEEM").Sum(a => a.UsdcSize),
                OldestTradeAt = trades.Count > 0 ? trades.Min(a => a.Timestamp) : null,
                NewestTradeAt = trades.Count > 0 ? trades.Max(a => a.Timestamp) : null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Polymarket lookup failed for {Wallet}.", Trader.WalletAddress);
            ErrorMessage = "Could not load live data from Polymarket.";
        }

        return Page();
    }

    private Guid GetFollowerId()
    {
        var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id)
            ? id
            : throw new InvalidOperationException("Authenticated user has no Supabase id claim.");
    }
}
