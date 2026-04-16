# Polymarket Copy-Trading Platform

ASP.NET Core Razor Pages app (.NET 10) for copying trades of successful Polymarket traders.

## Build & Run

```bash
dotnet build
dotnet run --project PolymtkCp/PolymtkCp.csproj
```

- Dev URLs: `https://localhost:7164` / `http://localhost:5287`
- Default solution: `PolymtkCp.slnx`

## Architecture

**Razor Pages** — each feature lives in `PolymtkCp/Pages/` as a `.cshtml` + `.cshtml.cs` pair.

```
PolymtkCp/
  Pages/           # Razor Pages (UI + page model)
  wwwroot/         # Static assets (Bootstrap 5, jQuery, custom CSS/JS)
  Program.cs       # App bootstrap and DI configuration
  appsettings.json # Configuration (add connection strings, API keys here)
```

Implemented:
- **Supabase Auth** — login/logout via `Pages/Account/Login.cshtml` and `Logout.cshtml.cs`; cookie session stored under the `"Cookies"` scheme

Planned:
- **Polymarket API client** — REST client for markets, positions, and trade history
- **Copy-trading engine** — logic to mirror positions of tracked traders

## Conventions

- `nullable` and `implicit usings` are enabled — no need to add `using System` or null-suppress blindly
- Page models use `namespace PolymtkCp.Pages` (or sub-namespace matching the folder)
- Secrets belong in `appsettings.Development.json` (gitignored) or User Secrets — never hard-code API keys
- Use `ILogger<T>` injected via constructor for logging
- Prefer `async`/`await` for all I/O-bound operations (API calls, DB queries)

## Key External APIs (to integrate)

| API | Purpose | Base URL |
|-----|---------|---------|
| Polymarket CLOB API | Market data, positions, trade history | `https://clob.polymarket.com` |
| Polymarket Gamma API | Market search and metadata | `https://gamma-api.polymarket.com` |

## Database (to add)

SQL Server via EF Core. Connection string goes in `appsettings.json` under `"ConnectionStrings:DefaultConnection"`. Register with:

```csharp
builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
```
