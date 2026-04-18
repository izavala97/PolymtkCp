using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PolymtkCp.Models;
using Supabase.Postgrest;

namespace PolymtkCp.Pages.Traders;

public class IndexModel : PageModel
{
    private readonly Supabase.Client _supabase;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(Supabase.Client supabase, ILogger<IndexModel> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    public record PlanRow(CopyPlan Plan, Trader? Trader);

    public List<PlanRow> Rows { get; private set; } = [];
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        try
        {
            var followerId = GetFollowerId();

            var plansResp = await _supabase
                .From<CopyPlan>()
                .Where(p => p.FollowerId == followerId)
                .Order(p => p.CreatedAt, Constants.Ordering.Descending)
                .Get();

            var plans = plansResp.Models;
            if (plans.Count == 0)
                return;

            var traderIds = plans.Select(p => p.TraderId).Distinct().ToList();

            // RLS on `traders` allows authenticated users to read all rows, so
            // a single batched fetch by id list is fine.
            var tradersResp = await _supabase
                .From<Trader>()
                .Filter("id", Constants.Operator.In, traderIds.Select(g => g.ToString()).ToList())
                .Get();

            var byId = tradersResp.Models.ToDictionary(t => t.Id);

            Rows = plans
                .Select(p => new PlanRow(p, byId.TryGetValue(p.TraderId, out var t) ? t : null))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load copy plans.");
            ErrorMessage = "Could not load your traders. Please try again.";
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        try
        {
            var followerId = GetFollowerId();
            await _supabase
                .From<CopyPlan>()
                .Where(p => p.Id == id && p.FollowerId == followerId)
                .Delete();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete copy plan {PlanId}.", id);
            ErrorMessage = "Could not remove the trader. Please try again.";
        }

        return RedirectToPage();
    }

    private Guid GetFollowerId()
    {
        var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id)
            ? id
            : throw new InvalidOperationException("Authenticated user has no Supabase id claim.");
    }
}
