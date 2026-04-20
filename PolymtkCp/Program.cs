using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Supabase config (publishable + secret keys; see https://supabase.com/docs/guides/api/api-keys).
var supabaseUrl = builder.Configuration["Supabase:Url"] ?? throw new InvalidOperationException("Supabase:Url is not configured.");
var supabaseKey = builder.Configuration["Supabase:PublishableKey"]
    ?? throw new InvalidOperationException("Supabase:PublishableKey is not configured.");

// Per-request Supabase client. Per the official docs (server-side wiki),
// we create a fresh client per request and pass the user's JWT through
// SupabaseOptions.Headers["Authorization"]. The Supabase client's
// GetAuthHeaders() prefers options.Headers["Authorization"] when present
// and forwards it to all sub-clients (Postgrest, Storage, etc.), so RLS
// (auth.uid()) works for every read AND write through the SDK.
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient(); // generic factory used by the refresh middleware
builder.Services.AddScoped(sp =>
{
    var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
    // Prefer the freshly-refreshed token stashed by SupabaseSessionRefreshMiddleware,
    // fall back to the (possibly stale) claim value.
    var accessToken =
        http?.Items[PolymtkCp.Services.SupabaseSessionRefreshMiddleware.AccessTokenItemKey] as string
        ?? http?.User.FindFirst("supabase:access_token")?.Value;

    var options = new Supabase.SupabaseOptions
    {
        AutoConnectRealtime = false,
    };
    if (!string.IsNullOrEmpty(accessToken))
        options.Headers["Authorization"] = $"Bearer {accessToken}";

    return new Supabase.Client(supabaseUrl, supabaseKey, options);
});

// Polymarket public Data API client
builder.Services.AddHttpClient<PolymtkCp.Services.Polymarket.PolymarketClient>(c =>
{
    c.BaseAddress = new Uri(PolymtkCp.Services.Polymarket.PolymarketClient.DataApiBaseUrl);
    c.Timeout = TimeSpan.FromSeconds(15);
});

// Polygon RPC client (reads on-chain USDC.e balance = Polymarket "Cash")
builder.Services.AddHttpClient<PolymtkCp.Services.Polymarket.PolygonUsdcClient>(c =>
{
    var rpcUrl = builder.Configuration["Polygon:RpcUrl"];
    if (string.IsNullOrWhiteSpace(rpcUrl))
        rpcUrl = PolymtkCp.Services.Polymarket.PolygonUsdcClient.DefaultRpcUrl;
    c.BaseAddress = new Uri(rpcUrl);
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("PolymtkCp/0.1");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});

// ---------------------------------------------------------------------------
// Trader-activity watcher (background service).
// Uses the Supabase SERVICE ROLE key so it can read every Follower's plans
// and write copy_trade_executions across all rows (bypasses RLS). Only this
// background service should ever use the service-role client. HTTP request
// code paths must keep using the per-request Supabase.Client above.
// ---------------------------------------------------------------------------
builder.Services.Configure<PolymtkCp.Services.Watcher.WatcherOptions>(
    builder.Configuration.GetSection(PolymtkCp.Services.Watcher.WatcherOptions.SectionName));

var watcherOpts = builder.Configuration
    .GetSection(PolymtkCp.Services.Watcher.WatcherOptions.SectionName)
    .Get<PolymtkCp.Services.Watcher.WatcherOptions>()
    ?? new PolymtkCp.Services.Watcher.WatcherOptions();
var serviceRoleKey = builder.Configuration["Supabase:SecretKey"];

if (watcherOpts.Enabled && !string.IsNullOrEmpty(serviceRoleKey))
{
    builder.Services.AddSingleton(_ =>
    {
        var client = new Supabase.Client(supabaseUrl, serviceRoleKey, new Supabase.SupabaseOptions
        {
            AutoConnectRealtime = false,
        });
        // Service role goes in the Authorization header for PostgREST writes.
        client.Postgrest.GetHeaders = () => new Dictionary<string, string>
        {
            ["apikey"] = serviceRoleKey,
            ["Authorization"] = $"Bearer {serviceRoleKey}",
        };
        return new PolymtkCp.Services.Watcher.WatcherSupabase(client);
    });
    builder.Services.AddHostedService<PolymtkCp.Services.Watcher.TraderWatcher>();
}
else
{
    // Surface a single startup log line; don't crash a dev environment that
    // hasn't been wired up yet.
    builder.Logging.AddFilter("PolymtkCp.Services.Watcher", LogLevel.Information);
    Console.WriteLine("[startup] TraderWatcher disabled (Supabase:SecretKey missing or Watcher:Enabled=false).");
}

// ---------------------------------------------------------------------------
// ASP.NET Data Protection — encrypts per-Follower Polymarket L2 credentials
// at rest. In production: key ring persisted to Azure Blob Storage, wrapped
// (KEK) by an Azure Key Vault key. Auto-rotation every 90 days is the
// Data Protection default. In dev: falls back to the local file system with
// no KEK if either config key is missing — logged loudly, blocked in
// production.
// ---------------------------------------------------------------------------
{
    var blobUri  = builder.Configuration["DataProtection:BlobStorageUri"];
    var kvKeyId  = builder.Configuration["DataProtection:KeyVaultKeyId"];
    var dpBuilder = builder.Services.AddDataProtection().SetApplicationName("PolymtkCp");

    if (!string.IsNullOrWhiteSpace(blobUri) && !string.IsNullOrWhiteSpace(kvKeyId))
    {
        var credential = new Azure.Identity.DefaultAzureCredential();
        dpBuilder
            .PersistKeysToAzureBlobStorage(new Uri(blobUri), credential)
            .ProtectKeysWithAzureKeyVault(new Uri(kvKeyId), credential);
    }
    else
    {
        if (builder.Environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Data Protection is not configured. In production, both " +
                "DataProtection:BlobStorageUri and DataProtection:KeyVaultKeyId must be set.");
        }
        Console.WriteLine(
            "[startup] WARNING: Data Protection running in DEV fallback mode " +
            "(local file system, no KEK). Do NOT ship this configuration.");
    }
}

builder.Services.AddScoped<PolymtkCp.Services.Secrets.IFollowerSecretStore,
                          PolymtkCp.Services.Secrets.FollowerSecretStore>();

// Cookie-based authentication backed by Supabase Auth
builder.Services.AddAuthentication("Cookies")
    .AddCookie("Cookies", options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    });

// Add services to the container.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<PolymtkCp.Filters.RequireWalletPageFilter>();
builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o =>
    o.Filters.AddService<PolymtkCp.Filters.RequireWalletPageFilter>());
builder.Services.AddRazorPages(opts =>
{
    opts.Conventions.AuthorizeFolder("/Traders");
    opts.Conventions.AuthorizePage("/Account/Profile");
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseRouting();

app.UseAuthentication();
app.UseMiddleware<PolymtkCp.Services.SupabaseSessionRefreshMiddleware>();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
