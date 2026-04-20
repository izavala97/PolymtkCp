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
// Phase-2 OrderExecutor (background service).
// Reads pending real-mode rows produced by the watcher, decrypts the Follower's
// Polymarket credentials, and submits orders to the CLOB. Same service-role
// requirement as the watcher (cross-Follower reads + writes); the singleton
// WatcherSupabase registered above is reused.
// ---------------------------------------------------------------------------
builder.Services.Configure<PolymtkCp.Services.Executor.ExecutorOptions>(
    builder.Configuration.GetSection(PolymtkCp.Services.Executor.ExecutorOptions.SectionName));

var executorOpts = builder.Configuration
    .GetSection(PolymtkCp.Services.Executor.ExecutorOptions.SectionName)
    .Get<PolymtkCp.Services.Executor.ExecutorOptions>()
    ?? new PolymtkCp.Services.Executor.ExecutorOptions();

if (executorOpts.Enabled && !string.IsNullOrEmpty(serviceRoleKey))
{
    // Reuse the WatcherSupabase service-role client. The OrderExecutor reads the
    // same table the watcher writes; sharing the singleton keeps connection counts down.
    builder.Services.AddSingleton(sp =>
    {
        var supa = sp.GetRequiredService<PolymtkCp.Services.Watcher.WatcherSupabase>();
        var dpProvider = sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
        var logger = sp.GetRequiredService<ILogger<PolymtkCp.Services.Executor.ExecutorSecretReader>>();
        return new PolymtkCp.Services.Executor.ExecutorSecretReader(supa.Client, dpProvider, logger);
    });

    builder.Services.AddHttpClient<PolymtkCp.Services.Executor.PolymarketClobClient>((sp, c) =>
    {
        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PolymtkCp.Services.Executor.ExecutorOptions>>().Value;
        c.BaseAddress = new Uri(opts.ClobBaseUrl);
        c.Timeout = TimeSpan.FromSeconds(20);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PolymtkCp/0.1");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    });

    builder.Services.AddHostedService<PolymtkCp.Services.Executor.OrderExecutor>();
}
else
{
    Console.WriteLine("[startup] OrderExecutor disabled (Supabase:SecretKey missing or Executor:Enabled=false).");
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
        // Harden auth cookie: always over HTTPS in non-dev, not accessible from JS,
        // and SameSite=Lax so third-party iframes can't replay it while keeping
        // same-site POSTs (login form -> home) working.
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    });

// Rate limiting for sensitive auth / credential endpoints. .NET's built-in
// System.Threading.RateLimiting; strict buckets on login + credential save,
// wider bucket everywhere else so normal browsing stays snappy.
builder.Services.AddRateLimiter(opts =>
{
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Strict: 10 attempts / 5 min per IP for auth + credential writes.
    opts.AddPolicy("auth-strict", httpCtx =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpCtx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
            }));
});

// Add services to the container.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<PolymtkCp.Services.Polymarket.TraderStatsService>();
builder.Services.AddScoped<PolymtkCp.Filters.RequireWalletPageFilter>();
builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o =>
    o.Filters.AddService<PolymtkCp.Filters.RequireWalletPageFilter>());
builder.Services.AddRazorPages(opts =>
{
    opts.Conventions.AuthorizeFolder("/Traders");
    opts.Conventions.AuthorizePage("/Account/Profile");
    // Throttle brute-force attempts on auth + credential-write pages.
    foreach (var path in new[]
    {
        "/Account/Login",
        "/Account/Register",
        "/Account/ForgotPassword",
        "/Account/ResetPassword",
        "/Account/Profile",
    })
    {
        opts.Conventions.AddPageApplicationModelConvention(path, model =>
            model.EndpointMetadata.Add(
                new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("auth-strict")));
    }
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

// Baseline security response headers. Framework-agnostic, cheap, and prevents
// a class of clickjacking / MIME-sniff / referrer-leak attacks that otherwise
// depend on client defaults.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"]        = "strict-origin-when-cross-origin";
    h["X-Frame-Options"]        = "DENY";
    h["Permissions-Policy"]     = "geolocation=(), microphone=(), camera=()";
    await next();
});

app.UseRouting();

app.UseRateLimiter();

app.UseAuthentication();
app.UseMiddleware<PolymtkCp.Services.SupabaseSessionRefreshMiddleware>();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
