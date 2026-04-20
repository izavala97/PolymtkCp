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
  Components/
    BalancePillViewComponent.cs       # navbar Portfolio / Cash / Positions pill
  Filters/
    RequireWalletPageFilter.cs        # redirect to /Account/Profile if wallet missing
  Models/                             # Supabase.Postgrest.BaseModel DTOs
    Trader.cs
    CopyPlan.cs
    FollowerProfile.cs
    CopyTradeExecution.cs
  Pages/
    Account/  Login, Register, Logout, ForgotPassword, ResetPassword, Profile
    Traders/  Index, Add, Detail
    Shared/   _Layout.cshtml + Components/BalancePill/Default.cshtml
  Services/
    Polymarket/
      PolymarketClient.cs             # data-api.polymarket.com (positions, activity, value)
      PolymarketDtos.cs               # JSON DTOs
      PolygonUsdcClient.cs            # eth_call balanceOf for USDC.e on Polygon
    Watcher/
      TraderWatcher.cs                # IHostedService polling each active CopyPlan's Trader
      WatcherSupabase.cs              # SERVICE-ROLE Supabase client (bypasses RLS) used only here
      WatcherOptions.cs               # Watcher:* config (PollInterval, ActivityPageSize, Enabled)
    SupabaseSessionRefreshMiddleware.cs  # refresh JWT before downstream code runs
    WalletInput.cs                    # parse + normalize wallet input
  Program.cs                          # DI, auth, middleware pipeline, per-request Supabase.Client
  appsettings.json                    # Configuration (Supabase URL/key, Polygon RPC override)
supabase/
  migrations/
    20260418060000_initial_schema.sql # consolidated initial schema
  reset-dev.sql                       # DEV-ONLY: drops app tables + migration history
```

### Implemented (phase 1 — public APIs only, no key handling)
- **Supabase Auth** — email/password login, register, logout, forgot/reset password (Pages/Account)
- **Per-request Supabase client** — built fresh per request with the user JWT in `SupabaseOptions.Headers["Authorization"]`. RLS works for both reads and writes through the SDK
- **`SupabaseSessionRefreshMiddleware`** — refreshes the Supabase access token via `auth/v1/token?grant_type=refresh_token` whenever it's within 2 minutes of expiry, stashes the fresh token in `HttpContext.Items` (consumed by the Supabase factory), and re-issues the auth cookie. Eliminates `PGRST303 JWT expired` after the ~1h token lifetime
- **Profile + balance display** — `Portfolio = Cash + Positions value`. Cash comes from an on-chain USDC.e `balanceOf(wallet)` `eth_call` to a public Polygon RPC. Positions value comes from Polymarket's `/value` endpoint. Both run in parallel, are 60s-cached, and tolerate partial failure
- **Traders dashboard** — add/remove Traders, configure sizing/limits/expiry, view per-Trader detail page with copy-plan summary, copy-trade history, open positions, and recent activity. Polymarket deep-links throughout
- **`TraderWatcher`** (`IHostedService`) — polls each active CopyPlan's Trader on `data-api.polymarket.com/activity?user=...`, diffs against `copy_trade_executions.source_activity_hash`, scales per the plan's sizing (`fixed` or `percent`), enforces daily ops/money limits and expiry, and writes rows with `mode='paper'` and `status='simulated'` (or `'skipped'` with a human-readable reason). Only emits trades that occurred at/after the plan's `created_at` (no historical replay). Runs only when `Supabase:ServiceRoleKey` is set

### Deferred (phase 2 — trade execution)
- **Auto-execution service** — places real orders on the Follower's account using their stored, encrypted private key (server signs and submits to the CLOB). Will read pending `mode='real'` rows produced by an extended watcher

## Domain glossary

- **Follower** — logged-in user of this app whose Polymarket account places copied trades
- **Trader** — public Polymarket trader being copied; identified by wallet address. Shared across all Followers (one row per wallet)
- **CopyPlan** — a Follower's per-Trader configuration: sizing, daily limits, expiration, active flag. One row per (Follower, Trader) pair
- **CopyTradeExecution** — append-only record of one copy-trade decision (`mode` paper/real, `status` simulated/skipped/pending/submitted/filled/failed)

Use these terms consistently in code, database columns, and UI copy.

## Conventions

- `nullable` and `implicit usings` are enabled — no need to add `using System` or null-suppress blindly
- Page models use `namespace PolymtkCp.Pages` (or sub-namespace matching the folder)
- Secrets belong in User Secrets (dev) or environment variables / Azure App Settings (prod) — never hard-code, never commit
- **The server does not handle Polymarket private keys or L2 API credentials in the current phase.** The app is read-only against the public Polymarket APIs. Key storage and order placement land in phase 2 (encrypted at rest via ASP.NET Data Protection + Azure Key Vault)
- Use `ILogger<T>` injected via constructor for logging
- Prefer `async`/`await` for all I/O-bound operations (API calls, DB queries)
- Use the existing per-request `Supabase.Client` (DI scoped) for all DB access **from HTTP request handlers**. Do **not** instantiate `Supabase.Client` manually — that bypasses the JWT injection and breaks RLS
- The watcher is the only place that uses the SERVICE ROLE key (`WatcherSupabase`). Don't introduce other consumers of the service-role client; if you need cross-Follower data outside the watcher, write a SECURITY DEFINER SQL function instead
- Supabase model PKs that you actually want to send in INSERTs (e.g. `FollowerProfile.FollowerId = auth.uid()`) must use `[PrimaryKey("col", true)]`. The default `false` excludes the PK from INSERT bodies (it's intended for DB-generated identity columns)

## Key External APIs

| API | Purpose | Auth |
|-----|---------|------|
| Polymarket Gamma (`https://gamma-api.polymarket.com`) | Market / event metadata | Public |
| Polymarket Data (`https://data-api.polymarket.com`) | `/positions`, `/activity`, `/value` for any wallet | Public |
| Polymarket CLOB (`https://clob.polymarket.com`) | *(phase 2)* place / cancel orders | L1 + L2 |
| Polygon RPC (`https://1rpc.io/matic` by default) | `eth_call` USDC.e `balanceOf(wallet)` for Cash | Public |

`polygon-rpc.com` no longer works without an API key (returns "tenant disabled"). The default RPC is overridable via the `Polygon:RpcUrl` config key.

See [README.md](README.md) for the full feature spec, position lifecycle, and copy-trading semantics.

## Database

**Supabase Postgres only.** Use the per-request `Supabase.Client` (registered in `Program.cs` as scoped, with the user's JWT attached via `SupabaseOptions.Headers["Authorization"]` for RLS) and model tables with `Supabase.Postgrest` `[Table]` / `[Column]` attributes.

### Tables

- `traders` — shared across all Followers (one row per wallet). Authenticated users can read/insert/update; deletion is not allowed from the app
- `copy_plans` — one row per (Follower, Trader). Standard own-row RLS via `auth.uid() = follower_id`
- `follower_profiles` — one row per Follower (`follower_id` PK). Holds `polymarket_wallet_address`. `encrypted_api_key` reserved for phase 2
- `copy_trade_executions` — append-only log of copy-trade decisions; `event_title`/`outcome`/`slug` are denormalized for fast rendering. Own-row RLS; no DELETE policy

All tables have `created_at` / `updated_at` timestamps; the `set_updated_at()` trigger maintains `updated_at` on every update.

### Migrations — Supabase CLI

Schema lives in `supabase/migrations/` as timestamped `.sql` files committed to the repo. We do **not** use EF Core; raw SQL keeps RLS, policies, and triggers first-class. The current state is a single consolidated initial migration.

One-time setup:

```bash
scoop install supabase           # or: npm i -g supabase
supabase login                   # browser auth
supabase link --project-ref <ref>
```

Day-to-day workflow:

```bash
supabase migration new <name>    # creates supabase/migrations/<timestamp>_<name>.sql
# edit the .sql file
supabase db push                 # apply pending migrations to the linked remote
supabase migration list          # see what's applied vs pending
```

Never edit a migration after it has been pushed — add a new one instead.

### Dev-only reset

`supabase/reset-dev.sql` drops the four app tables + the `set_updated_at()` function + the migration history rows for the consolidated initial schema. Paste it into the Supabase Dashboard → SQL Editor when you want to wipe and re-apply from scratch. **Never run this in production.**

## Watcher configuration

The `TraderWatcher` background service is registered conditionally based on `appsettings`:

```jsonc
{
  "Supabase": {
    "Url": "https://<ref>.supabase.co",
    "AnonKey": "...",            // for the per-request client
    "ServiceRoleKey": "..."      // REQUIRED for the watcher to start
  },
  "Watcher": {
    "Enabled": true,
    "PollInterval": "00:10:00",  // TimeSpan
    "ActivityPageSize": 50
  }
}
```

- The service-role key bypasses RLS — keep it in User Secrets / env vars, never commit it
- If `ServiceRoleKey` is missing, the app starts normally but the watcher is skipped (a startup log line says so)
- The watcher only emits copy-trades that occurred **at or after** the plan's `created_at`, so adding a new plan does not retroactively flood the history with the trader's last 50 trades
- All emitted rows are `mode='paper'` in phase 1. Real-mode emission ships with the phase-2 executor
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

In progress (current phase — public-API only, no key handling):
- **Polymarket watcher** — polls a Trader's wallet via the public Data API and emits proposed copy-trades
- **Dashboard** — Razor Pages to add/remove Traders, view their positions, and review proposed trades

Deferred (future phase — trade execution):
- **Auto-execution service** — places real orders on the Follower's account using their stored, encrypted private key (server signs and submits)

## Domain glossary

- **Follower** — logged-in user of this app whose Polymarket account places copied trades
- **Trader** — public Polymarket trader being copied; identified by wallet address. Shared across all Followers (one row per wallet)
- **CopyPlan** — a Follower's per-Trader configuration: sizing, daily limits, expiration, active flag. One row per (Follower, Trader) pair

Use these terms consistently in code (`Follower`, `Trader`, `CopyPlan`), database columns, and UI copy.

## Conventions

- `nullable` and `implicit usings` are enabled — no need to add `using System` or null-suppress blindly
- Page models use `namespace PolymtkCp.Pages` (or sub-namespace matching the folder)
- Secrets belong in User Secrets (dev) or environment variables / Azure App Settings (prod) — never hard-code, never commit
- **The server does not handle Polymarket private keys or L2 API credentials in the current phase.** The app is read-only against the public Polymarket APIs. Key storage and order placement land in phase 2 (encrypted at rest via ASP.NET Data Protection + Azure Key Vault).
- Use `ILogger<T>` injected via constructor for logging
- Prefer `async`/`await` for all I/O-bound operations (API calls, DB queries)

## Key External APIs

| API | Purpose | Auth |
|-----|---------|------|
| Polymarket Gamma (`https://gamma-api.polymarket.com`) | Market search and metadata | Public |
| Polymarket Data (`https://data-api.polymarket.com`) | Positions / activity for any wallet (used to watch Traders) | Public |
| Polymarket CLOB (`https://clob.polymarket.com`) | *(phase 2)* place / cancel orders | L1 + L2 |

See [README.md](README.md) for the full feature spec, position lifecycle, and copy-trading semantics.

## Database

**Supabase Postgres only.** Use the `Supabase.Client` (registered in `Program.cs` as scoped, with the user's JWT attached for RLS) and model tables with `Supabase.Postgrest` `[Table]` / `[Column]` attributes.

### Tables

- `traders` — shared across all Followers (one row per wallet). Authenticated users can read/insert/update; deletion is not allowed from the app.
- `copy_plans` — one row per (Follower, Trader). Standard own-row RLS via `auth.uid() = follower_id`.

All tables have `created_at` / `updated_at` timestamps; the `set_updated_at()` trigger maintains `updated_at` on every update.

### Migrations — Supabase CLI

Schema lives in `supabase/migrations/` as timestamped `.sql` files committed to the repo. We do **not** use EF Core; raw SQL keeps RLS, policies, and triggers first-class.

One-time setup:

```bash
scoop install supabase           # or: npm i -g supabase
supabase login                   # browser auth
supabase link --project-ref <ref>
```

Day-to-day workflow:

```bash
supabase migration new <name>    # creates supabase/migrations/<timestamp>_<name>.sql
# edit the .sql file
supabase db push                 # apply pending migrations to the linked remote
supabase migration list          # see what's applied vs pending
```

Never edit a migration after it has been pushed — add a new one instead.
