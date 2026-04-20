using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PolymtkCp.Models;
using PolymtkCp.Services.Secrets;

namespace PolymtkCp.Pages.Traders;

public class EditModel : PageModel
{
    private readonly Supabase.Client _supabase;
    private readonly IFollowerSecretStore _secretStore;
    private readonly ILogger<EditModel> _logger;

    public EditModel(Supabase.Client supabase, IFollowerSecretStore secretStore, ILogger<EditModel> logger)
    {
        _supabase = supabase;
        _secretStore = secretStore;
        _logger = logger;
    }

    public Guid PlanId { get; private set; }
    public string? TraderLabel { get; private set; }

    /// <summary>
    /// True only when the Follower has saved an L2 triple AND a wallet private key.
    /// Drives the Real-mode radio in the view; also enforced server-side in OnPost.
    /// </summary>
    public bool RealModeAvailable { get; private set; }

    [BindProperty] public bool IsActive { get; set; } = true;

    [BindProperty] [Required] public string Mode { get; set; } = "paper";

    [BindProperty] [Required] public string SizingMode { get; set; } = "fixed";

    [BindProperty]
    [Range(0.01, 10_000, ErrorMessage = "Must be between $0.01 and $10,000.")]
    public decimal? FixedAmountUsd { get; set; }

    [BindProperty]
    [Range(0.1, 100, ErrorMessage = "Must be between 0.1% and 100%.")]
    public decimal? PercentOfNotional { get; set; }

    [BindProperty]
    [Range(1, 1000, ErrorMessage = "Must be between 1 and 1000.")]
    [Display(Name = "Daily operations limit")]
    public int? DailyTradeOperationsLimit { get; set; }

    [BindProperty]
    [Range(1, 1_000_000, ErrorMessage = "Must be between $1 and $1,000,000.")]
    [Display(Name = "Daily money limit (USDC)")]
    public decimal? DailyTradeMoneyLimit { get; set; }

    [BindProperty]
    [Range(2, 1000, ErrorMessage = "Must be between 2 and 1000.")]
    [Display(Name = "Group similar ops (N)")]
    public int? GroupSimilarOps { get; set; }

    [BindProperty]
    [DataType(DataType.Date)]
    [Display(Name = "Plan expires on")]
    public DateTime? ExpiresAt { get; set; }

    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var followerId = GetFollowerId();
        var plan = await _supabase
            .From<CopyPlan>()
            .Where(p => p.Id == id && p.FollowerId == followerId)
            .Single();
        if (plan is null) return NotFound();

        PlanId = plan.Id;
        IsActive = plan.IsActive;
        Mode = plan.Mode;
        SizingMode = plan.SizingMode;
        FixedAmountUsd = plan.FixedAmountUsd;
        PercentOfNotional = plan.PercentOfNotional;
        DailyTradeOperationsLimit = plan.DailyTradeOperationsLimit;
        DailyTradeMoneyLimit = plan.DailyTradeMoneyLimit;
        GroupSimilarOps = plan.GroupSimilarOps;
        ExpiresAt = plan.ExpiresAt;

        await LoadTraderLabelAsync(plan.TraderId);
        await LoadRealModeAvailabilityAsync(followerId);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        PlanId = id;
        var followerId = GetFollowerId();

        if (SizingMode == "fixed" && FixedAmountUsd is null or <= 0)
            ModelState.AddModelError(nameof(FixedAmountUsd), "Required when using fixed sizing.");
        if (SizingMode == "percent" && PercentOfNotional is null or <= 0)
            ModelState.AddModelError(nameof(PercentOfNotional), "Required when using percent sizing.");
        if (ExpiresAt is { } exp && exp.Date <= DateTime.UtcNow.Date)
            ModelState.AddModelError(nameof(ExpiresAt), "Expiration must be in the future.");

        var existing = await _supabase
            .From<CopyPlan>()
            .Where(p => p.Id == id && p.FollowerId == followerId)
            .Single();
        if (existing is null) return NotFound();

        await LoadTraderLabelAsync(existing.TraderId);
        await LoadRealModeAvailabilityAsync(followerId);

        // Server-side gate: don't trust the client. If Real mode isn't available,
        // silently coerce back to paper rather than 400'ing — this also handles
        // the user revoking credentials between page load and form submit.
        if (Mode == "real" && !RealModeAvailable)
        {
            Mode = "paper";
            ErrorMessage = "Real mode requires saved Polymarket credentials including a wallet private key. Plan reverted to Paper.";
        }

        if (!ModelState.IsValid) return Page();

        try
        {
            existing.IsActive = IsActive;
            existing.Mode = Mode == "real" ? "real" : "paper";
            existing.SizingMode = SizingMode;
            existing.FixedAmountUsd = SizingMode == "fixed" ? FixedAmountUsd : null;
            existing.PercentOfNotional = SizingMode == "percent" ? PercentOfNotional : null;
            existing.DailyTradeOperationsLimit = DailyTradeOperationsLimit;
            existing.DailyTradeMoneyLimit = DailyTradeMoneyLimit;
            existing.GroupSimilarOps = GroupSimilarOps;
            existing.ExpiresAt = ExpiresAt is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

            await existing.Update<CopyPlan>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update copy plan {PlanId}.", id);
            ErrorMessage = "Could not save changes. Please try again.";
            return Page();
        }

        return RedirectToPage("Detail", new { id });
    }

    private async Task LoadTraderLabelAsync(Guid traderId)
    {
        try
        {
            var t = await _supabase.From<Trader>().Where(t => t.Id == traderId).Single();
            if (t is not null)
                TraderLabel = t.Nickname ?? t.WalletAddress;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Edit: failed to load trader {TraderId}.", traderId);
        }
    }

    private async Task LoadRealModeAvailabilityAsync(Guid followerId)
    {
        try
        {
            var status = await _secretStore.GetStatusAsync(followerId);
            RealModeAvailable = status.HasActive && status.HasPrivateKey;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Edit: failed to load secret status for {FollowerId}.", followerId);
            RealModeAvailable = false;
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
