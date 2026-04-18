using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace PolymtkCp.Services;

/// <summary>
/// Refreshes the Supabase access_token claim transparently if it's near
/// expiration. Runs once per request before any code that talks to Supabase.
/// Updates HttpContext.Items["supabase:access_token"] so the per-request
/// Supabase.Client factory in Program.cs picks up the fresh token, and
/// re-issues the auth cookie so future requests start with the new tokens.
/// </summary>
public sealed class SupabaseSessionRefreshMiddleware
{
    public const string AccessTokenItemKey = "supabase:access_token";
    private static readonly TimeSpan RefreshIfWithin = TimeSpan.FromMinutes(2);

    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SupabaseSessionRefreshMiddleware> _logger;

    public SupabaseSessionRefreshMiddleware(
        RequestDelegate next,
        IConfiguration config,
        IHttpClientFactory httpFactory,
        ILogger<SupabaseSessionRefreshMiddleware> logger)
    {
        _next = next;
        _config = config;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            var accessToken = ctx.User.FindFirst("supabase:access_token")?.Value;
            var refreshToken = ctx.User.FindFirst("supabase:refresh_token")?.Value;

            if (!string.IsNullOrEmpty(accessToken))
            {
                ctx.Items[AccessTokenItemKey] = accessToken;

                if (!string.IsNullOrEmpty(refreshToken) && NeedsRefresh(accessToken))
                {
                    var fresh = await TryRefreshAsync(refreshToken, ctx.RequestAborted);
                    if (fresh is not null)
                    {
                        ctx.Items[AccessTokenItemKey] = fresh.Value.AccessToken;
                        await ReissueCookieAsync(ctx, fresh.Value.AccessToken, fresh.Value.RefreshToken);
                    }
                }
            }
        }

        await _next(ctx);
    }

    private static bool NeedsRefresh(string jwt)
    {
        try
        {
            var token = new JwtSecurityTokenHandler().ReadJwtToken(jwt);
            return token.ValidTo - DateTime.UtcNow < RefreshIfWithin;
        }
        catch
        {
            return true; // unparseable = treat as expired
        }
    }

    private async Task<(string AccessToken, string RefreshToken)?> TryRefreshAsync(
        string refreshToken, CancellationToken ct)
    {
        var url = _config["Supabase:Url"];
        var anonKey = _config["Supabase:AnonKey"];
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(anonKey))
            return null;

        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                $"{url.TrimEnd('/')}/auth/v1/token?grant_type=refresh_token");
            req.Headers.Add("apikey", anonKey);
            req.Content = JsonContent.Create(new { refresh_token = refreshToken });

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Supabase token refresh failed: {Status}", resp.StatusCode);
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            var newAccess = root.GetProperty("access_token").GetString();
            var newRefresh = root.GetProperty("refresh_token").GetString();
            if (string.IsNullOrEmpty(newAccess) || string.IsNullOrEmpty(newRefresh))
                return null;

            return (newAccess, newRefresh);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Supabase token refresh threw.");
            return null;
        }
    }

    private static async Task ReissueCookieAsync(HttpContext ctx, string accessToken, string refreshToken)
    {
        // Rebuild claims, replacing the two token claims; keep everything else.
        var oldClaims = ctx.User.Claims
            .Where(c => c.Type is not "supabase:access_token" and not "supabase:refresh_token");
        var claims = new List<Claim>(oldClaims)
        {
            new("supabase:access_token", accessToken),
            new("supabase:refresh_token", refreshToken),
        };
        var identity = new ClaimsIdentity(claims, "Cookies");
        var principal = new ClaimsPrincipal(identity);
        await ctx.SignInAsync("Cookies", principal);
    }
}
