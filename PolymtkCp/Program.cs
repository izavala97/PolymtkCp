var builder = WebApplication.CreateBuilder(args);

// Supabase config
var supabaseUrl = builder.Configuration["Supabase:Url"] ?? throw new InvalidOperationException("Supabase:Url is not configured.");
var supabaseKey = builder.Configuration["Supabase:AnonKey"] ?? throw new InvalidOperationException("Supabase:AnonKey is not configured.");

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
    c.BaseAddress = new Uri(builder.Configuration["Polygon:RpcUrl"]
        ?? PolymtkCp.Services.Polymarket.PolygonUsdcClient.DefaultRpcUrl);
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("PolymtkCp/0.1");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});

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
