using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using PolymtkCp.Filters;
using PolymtkCp.Models;
using PolymtkCp.Services;
using PolymtkCp.Services.Polymarket;
using PolymtkCp.Services.Secrets;

namespace PolymtkCp.Pages.Account;

public class ProfileModel : PageModel
{
    private readonly Supabase.Client _supabase;
    private readonly PolymarketClient _polymarket;
    private readonly PolygonUsdcClient _polygon;
    private readonly IFollowerSecretStore _secretStore;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ProfileModel> _logger;

    public ProfileModel(
        Supabase.Client supabase,
        PolymarketClient polymarket,
        PolygonUsdcClient polygon,
        IFollowerSecretStore secretStore,
        IMemoryCache cache,
        ILogger<ProfileModel> logger)
    {
        _supabase = supabase;
        _polymarket = polymarket;
        _polygon = polygon;
        _secretStore = secretStore;
        _cache = cache;
        _logger = logger;
    }

    [BindProperty]
    [Display(Name = "Polymarket wallet address or profile URL")]
    public string? WalletInputText { get; set; }

    [BindProperty, Display(Name = "API key")]
    public string? CredApiKey { get; set; }

    [BindProperty, Display(Name = "API secret")]
    public string? CredSecret { get; set; }

    [BindProperty, Display(Name = "API passphrase")]
    public string? CredPassphrase { get; set; }

    public FollowerSecretStatus? CredentialsStatus { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Reason { get; set; }

    public string? Email { get; private set; }
    public string? CurrentWallet { get; private set; }
    public decimal? PositionsValue { get; private set; }
    public decimal? CashUsdc { get; private set; }
    public decimal? PortfolioTotal => PositionsValue is null && CashUsdc is null ? null : (PositionsValue ?? 0m) + (CashUsdc ?? 0m);
    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public bool WalletRequired => Reason == "wallet_required" && string.IsNullOrEmpty(CurrentWallet);

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();

        if (string.IsNullOrWhiteSpace(WalletInputText))
        {
            ModelState.AddModelError(nameof(WalletInputText), "Wallet address is required.");
            return Page();
        }

        var address = WalletInput.TryExtractAddress(WalletInputText);
        if (address is null)
        {
            ModelState.AddModelError(nameof(WalletInputText), "Could not find a 0x wallet address in your input.");
            return Page();
        }

        var normalized = address.ToLowerInvariant();

        try
        {
            await _polymarket.GetPortfolioValueAsync(normalized);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Polymarket lookup failed while saving profile wallet {Wallet}.", normalized);
            ErrorMessage = "Could not verify the wallet against Polymarket. Please try again.";
            return Page();
        }

        var followerId = GetFollowerId();

        try
        {
            // Upsert: insert if missing, otherwise update the wallet column.
            var existing = await _supabase
                .From<FollowerProfile>()
                .Where(p => p.FollowerId == followerId)
                .Single();

            if (existing is null)
            {
                await _supabase.From<FollowerProfile>().Insert(new FollowerProfile
                {
                    FollowerId = followerId,
                    PolymarketWalletAddress = normalized,
                });
            }
            else
            {
                await _supabase.From<FollowerProfile>()
                    .Where(p => p.FollowerId == followerId)
                    .Set(p => p.PolymarketWalletAddress!, normalized)
                    .Update();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save follower profile for {FollowerId}.", followerId);
            ErrorMessage = "Could not save your profile. Please try again.";
            return Page();
        }

        StatusMessage = "Wallet saved.";
        RequireWalletPageFilter.Invalidate(_cache, followerId);
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDisconnectAsync()
    {
        var followerId = GetFollowerId();

        try
        {
            await _supabase.From<FollowerProfile>()
                .Where(p => p.FollowerId == followerId)
                .Set(p => p.PolymarketWalletAddress!, null!)
                .Update();
            StatusMessage = "Wallet disconnected.";
            RequireWalletPageFilter.Invalidate(_cache, followerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect wallet for {FollowerId}.", followerId);
            ErrorMessage = "Could not disconnect the wallet. Please try again.";
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveCredentialsAsync()
    {
        var followerId = GetFollowerId();

        var apiKey     = (CredApiKey ?? string.Empty).Trim();
        var secret     = (CredSecret ?? string.Empty).Trim();
        var passphrase = (CredPassphrase ?? string.Empty).Trim();

        if (apiKey.Length == 0 || secret.Length == 0 || passphrase.Length == 0)
        {
            ErrorMessage = "All three credential fields are required.";
            await LoadAsync();
            return Page();
        }
        if (apiKey.Length > 512 || secret.Length > 512 || passphrase.Length > 512)
        {
            ErrorMessage = "Credential fields must be 512 characters or fewer.";
            await LoadAsync();
            return Page();
        }

        try
        {
            await _secretStore.SetAsync(followerId, new PolymarketCredentials(apiKey, secret, passphrase));
            StatusMessage = "Polymarket credentials saved.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Polymarket credentials for {FollowerId}.", followerId);
            ErrorMessage = "Could not save credentials. Please try again.";
        }

        // Clear posted secrets from the model so they don't round-trip back to the browser.
        CredApiKey = CredSecret = CredPassphrase = null;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostClearCredentialsAsync()
    {
        var followerId = GetFollowerId();
        try
        {
            await _secretStore.ClearAsync(followerId);
            StatusMessage = "Polymarket credentials cleared.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear Polymarket credentials for {FollowerId}.", followerId);
            ErrorMessage = "Could not clear credentials. Please try again.";
        }
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Email = User.FindFirst(ClaimTypes.Email)?.Value;
        var followerId = GetFollowerId();

        try
        {
            var profile = await _supabase
                .From<FollowerProfile>()
                .Where(p => p.FollowerId == followerId)
                .Single();

            CurrentWallet = profile?.PolymarketWalletAddress;

            if (!string.IsNullOrWhiteSpace(CurrentWallet))
            {
                var positionsTask = _polymarket.GetPortfolioValueAsync(CurrentWallet);
                var cashTask = _polygon.GetCashAsync(CurrentWallet);
                try { await Task.WhenAll(positionsTask, cashTask); } catch { /* surfaced per-field below */ }
                if (positionsTask.IsCompletedSuccessfully) PositionsValue = positionsTask.Result;
                if (cashTask.IsCompletedSuccessfully) CashUsdc = cashTask.Result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Profile load failed for {FollowerId}.", followerId);
        }

        try
        {
            CredentialsStatus = await _secretStore.GetStatusAsync(followerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Credentials status load failed for {FollowerId}.", followerId);
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
