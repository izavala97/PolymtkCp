using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PolymtkCp.Models;
using PolymtkCp.Services;
using PolymtkCp.Services.Polymarket;
using PolymtkCp.Services.Secrets;

namespace PolymtkCp.Pages.Traders;

public class AddModel : PageModel
{
    private readonly Supabase.Client _supabase;
    private readonly PolymarketClient _polymarket;
    private readonly IFollowerSecretStore _secretStore;
    private readonly ILogger<AddModel> _logger;

    public AddModel(
        Supabase.Client supabase,
        PolymarketClient polymarket,
        IFollowerSecretStore secretStore,
        ILogger<AddModel> logger)
    {
        _supabase = supabase;
        _polymarket = polymarket;
        _secretStore = secretStore;
        _logger = logger;
    }

    /// <summary>
    /// True only when the Follower has saved an L2 triple AND a wallet private
    /// key. Drives the Real-mode radio in the view; also enforced server-side
    /// in <see cref="OnPostAsync"/>. Mirrors the gate in <c>EditModel</c>.
    /// </summary>
    public bool RealModeAvailable { get; private set; }

    [BindProperty]
    [Required(ErrorMessage = "Wallet address or profile URL is required.")]
    [Display(Name = "Wallet")]
    public string WalletInputText { get; set; } = string.Empty;

    [BindProperty]
    [StringLength(50)]
    public string? Nickname { get; set; }

    [BindProperty]
    [Required]
    public string SizingMode { get; set; } = "fixed";

    [BindProperty]
    [Required]
    public string Mode { get; set; } = "paper";

    [BindProperty]
    [Range(0.01, 10_000, ErrorMessage = "Must be between $0.01 and $10,000.")]
    public decimal? FixedAmountUsd { get; set; } = 1m;

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

    public async Task OnGetAsync(string? wallet = null)
    {
        if (!string.IsNullOrWhiteSpace(wallet))
            WalletInputText = wallet;
        await LoadRealModeAvailabilityAsync(GetFollowerId());
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var followerId = GetFollowerId();
        await LoadRealModeAvailabilityAsync(followerId);

        // Cross-field validation: the chosen sizing mode requires its corresponding amount.
        if (SizingMode == "fixed" && FixedAmountUsd is null or <= 0)
            ModelState.AddModelError(nameof(FixedAmountUsd), "Required when using fixed sizing.");
        if (SizingMode == "percent" && PercentOfNotional is null or <= 0)
            ModelState.AddModelError(nameof(PercentOfNotional), "Required when using percent sizing.");

        if (ExpiresAt is { } exp && exp.Date <= DateTime.UtcNow.Date)
            ModelState.AddModelError(nameof(ExpiresAt), "Expiration must be in the future.");

        var address = WalletInput.TryExtractAddress(WalletInputText);
        if (address is null)
            ModelState.AddModelError(nameof(WalletInputText), "Could not find a 0x wallet address in your input.");

        // Server-side gate: don't trust the client. If Real mode isn't available,
        // silently coerce back to paper rather than 400'ing — this also handles
        // the user revoking credentials between page load and form submit.
        if (Mode == "real" && !RealModeAvailable)
        {
            Mode = "paper";
            ErrorMessage = "Real mode requires saved Polymarket credentials including a wallet private key. Plan created in Paper mode.";
        }

        if (!ModelState.IsValid)
            return Page();

        var normalizedAddress = address!.ToLowerInvariant();

        // Sanity-check the address against Polymarket — confirms the wallet exists and has activity.
        try
        {
            var positions = await _polymarket.GetPositionsAsync(normalizedAddress);
            _logger.LogInformation("Validated wallet {Wallet}: {Count} open positions.", normalizedAddress, positions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Polymarket lookup failed for {Wallet}.", normalizedAddress);
            ErrorMessage = "Could not reach Polymarket to verify the wallet. Please try again in a moment.";
            return Page();
        }

        Trader trader;
        try
        {
            trader = await GetOrCreateTraderAsync(normalizedAddress, Nickname);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upsert trader {Wallet}.", normalizedAddress);
            ErrorMessage = "Could not save the trader. Please try again.";
            return Page();
        }

        try
        {
            var plan = new CopyPlan
            {
                FollowerId = followerId,
                TraderId = trader.Id,
                Mode = Mode == "real" ? "real" : "paper",
                SizingMode = SizingMode,
                FixedAmountUsd = SizingMode == "fixed" ? FixedAmountUsd : null,
                PercentOfNotional = SizingMode == "percent" ? PercentOfNotional : null,
                DailyTradeOperationsLimit = DailyTradeOperationsLimit,
                DailyTradeMoneyLimit = DailyTradeMoneyLimit,
                GroupSimilarOps = GroupSimilarOps,
                ExpiresAt = ExpiresAt is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null,
                IsActive = true,
            };

            await _supabase.From<CopyPlan>().Insert(plan);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert copy plan for trader {TraderId}.", trader.Id);
            ErrorMessage = "Could not save the copy plan. You may already be copying this trader.";
            return Page();
        }

        return RedirectToPage("Index");
    }

    /// <summary>
    /// Find an existing Trader for this wallet, or create one. If a nickname was
    /// supplied and the existing row has none, we patch it in.
    /// </summary>
    private async Task<Trader> GetOrCreateTraderAsync(string walletAddress, string? nickname)
    {
        var trimmedNickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();

        var existing = await _supabase
            .From<Trader>()
            .Where(t => t.WalletAddress == walletAddress)
            .Single();

        if (existing is not null)
        {
            if (trimmedNickname is not null && string.IsNullOrWhiteSpace(existing.Nickname))
            {
                await _supabase.From<Trader>()
                    .Where(t => t.Id == existing.Id)
                    .Set(t => t.Nickname!, trimmedNickname)
                    .Update();
                existing.Nickname = trimmedNickname;
            }
            return existing;
        }

        var inserted = await _supabase.From<Trader>().Insert(new Trader
        {
            WalletAddress = walletAddress,
            Nickname = trimmedNickname,
        });

        return inserted.Models.Single();
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
            _logger.LogWarning(ex, "Add: failed to load secret status for {FollowerId}.", followerId);
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
