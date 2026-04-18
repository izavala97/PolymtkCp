using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using PolymtkCp.Models;
using PolymtkCp.Services.Polymarket;

namespace PolymtkCp.Components;

/// <summary>
/// Renders the navbar pill showing the logged-in Follower's Polymarket
/// portfolio value. Looks up the wallet from <c>follower_profiles</c> and
/// queries Polymarket's public Data API. Result is cached in-memory for a
/// short window to avoid an API call on every page render.
/// </summary>
public class BalancePillViewComponent : ViewComponent
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly Supabase.Client _supabase;
    private readonly PolymarketClient _polymarket;
    private readonly PolygonUsdcClient _polygon;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BalancePillViewComponent> _logger;

    public BalancePillViewComponent(
        Supabase.Client supabase,
        PolymarketClient polymarket,
        PolygonUsdcClient polygon,
        IMemoryCache cache,
        ILogger<BalancePillViewComponent> logger)
    {
        _supabase = supabase;
        _polymarket = polymarket;
        _polygon = polygon;
        _cache = cache;
        _logger = logger;
    }

    public sealed record BalancePillVm(
        string? WalletAddress,
        decimal? Positions,
        decimal? Cash,
        bool Failed)
    {
        public decimal? Portfolio => Positions is null && Cash is null ? null : (Positions ?? 0m) + (Cash ?? 0m);
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var followerId = GetFollowerIdOrNull();
        if (followerId is null)
            return View(new BalancePillVm(null, null, null, false));

        string? wallet;
        try
        {
            var profile = await _supabase
                .From<FollowerProfile>()
                .Where(p => p.FollowerId == followerId.Value)
                .Single();
            wallet = profile?.PolymarketWalletAddress;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load follower profile for balance pill.");
            return View(new BalancePillVm(null, null, null, true));
        }

        if (string.IsNullOrWhiteSpace(wallet))
            return View(new BalancePillVm(null, null, null, false));

        var cacheKey = $"pmkt:bal:{wallet}";
        if (_cache.TryGetValue(cacheKey, out (decimal? positions, decimal? cash) cached))
            return View(new BalancePillVm(wallet, cached.positions, cached.cash, false));

        // Fetch in parallel — each independent. Partial success is OK.
        decimal? positions = null, cash = null;
        var pTask = SafeAsync(() => _polymarket.GetPortfolioValueAsync(wallet), "positions");
        var cTask = SafeAsync(() => _polygon.GetCashAsync(wallet), "cash");
        await Task.WhenAll(pTask, cTask);
        positions = pTask.Result;
        cash = cTask.Result;

        // Only treat as a hard failure if BOTH calls failed.
        var failed = positions is null && cash is null;
        if (!failed)
            _cache.Set(cacheKey, (positions, cash), CacheTtl);
        return View(new BalancePillVm(wallet, positions, cash, failed));
    }

    private async Task<decimal?> SafeAsync(Func<Task<decimal>> call, string label)
    {
        try { return await call(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Balance pill: {Label} call failed.", label);
            return null;
        }
    }

    private Guid? GetFollowerIdOrNull()
    {
        var raw = ((ClaimsPrincipal)User).FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
