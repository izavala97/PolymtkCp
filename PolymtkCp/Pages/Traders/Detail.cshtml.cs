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
    public string? ErrorMessage { get; set; }

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
                .Limit(50)
                .Get();
            Executions = executionsResp.Models;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load executions for plan {PlanId}.", Plan.Id);
        }

        try
        {
            var positionsTask = _polymarket.GetPositionsAsync(Trader.WalletAddress);
            var activityTask = _polymarket.GetActivityAsync(Trader.WalletAddress, limit: 25);
            await Task.WhenAll(positionsTask, activityTask);

            Positions = (await positionsTask)
                .OrderByDescending(p => p.CurrentValue)
                .ToList();
            Activity = (await activityTask)
                .Where(a => a.Type == "TRADE")
                .OrderByDescending(a => a.TimestampUnix)
                .ToList();
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
