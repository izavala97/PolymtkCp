using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using PolymtkCp.Models;

namespace PolymtkCp.Filters;

/// <summary>
/// For authenticated users, ensures a Polymarket wallet is set in
/// <c>follower_profiles</c>. If missing, redirects to <c>/Account/Profile</c>.
/// Excludes the auth pages, the home page, and the profile itself so the user
/// has somewhere to land.
/// </summary>
public sealed class RequireWalletPageFilter : IAsyncPageFilter
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    // Pages reachable without a wallet. Compared case-insensitively against PageContext.Page.RelativePath.
    private static readonly HashSet<string> AllowedPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Pages/Index.cshtml",
        "/Pages/Privacy.cshtml",
        "/Pages/Error.cshtml",
        "/Pages/Account/Login.cshtml",
        "/Pages/Account/Logout.cshtml",
        "/Pages/Account/Register.cshtml",
        "/Pages/Account/ForgotPassword.cshtml",
        "/Pages/Account/ResetPassword.cshtml",
        "/Pages/Account/Profile.cshtml",
    };

    private readonly IMemoryCache _cache;

    public RequireWalletPageFilter(IMemoryCache cache) => _cache = cache;

    /// <summary>Invalidate the cached wallet state for a Follower (call after Profile save).</summary>
    public static void Invalidate(IMemoryCache cache, Guid followerId)
        => cache.Remove(CacheKey(followerId));

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }

        var page = context.ActionDescriptor.RelativePath;
        if (page is not null && AllowedPages.Contains(page))
        {
            await next();
            return;
        }

        if (!Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var followerId))
        {
            await next();
            return;
        }

        var hasWallet = await GetHasWalletAsync(context.HttpContext, followerId);
        if (!hasWallet)
        {
            context.Result = new RedirectToPageResult("/Account/Profile", new { reason = "wallet_required" });
            return;
        }

        await next();
    }

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    private async Task<bool> GetHasWalletAsync(HttpContext http, Guid followerId)
    {
        var key = CacheKey(followerId);
        if (_cache.TryGetValue(key, out bool cached))
            return cached;

        var supabase = http.RequestServices.GetRequiredService<Supabase.Client>();
        try
        {
            var profile = await supabase
                .From<FollowerProfile>()
                .Where(p => p.FollowerId == followerId)
                .Single();

            var has = !string.IsNullOrWhiteSpace(profile?.PolymarketWalletAddress);
            _cache.Set(key, has, CacheTtl);
            return has;
        }
        catch
        {
            // Fail open: don't block the page if Supabase is unreachable.
            return true;
        }
    }

    private static string CacheKey(Guid followerId) => $"profile:hasWallet:{followerId}";
}
