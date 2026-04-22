# Agent contract

Read this before touching the repo. It complements [README.md](README.md) and [DEPLOYMENT.md](DEPLOYMENT.md).

## Stack at a glance

ASP.NET Core Razor Pages on .NET 10 (`PolymtkCp/`) + Supabase Postgres+Auth + Polymarket APIs (Gamma/Data/CLOB) + Polygon RPC + Nethereum (EIP-712 signing) + ASP.NET Data Protection (Azure Blob keyring + Key Vault KEK in prod). Tests in `PolymtkCp.Tests/` (xUnit). DB schema in `supabase/migrations/` (raw SQL).

## Layout

```
PolymtkCp/
  Components/     view components (BalancePill)
  Filters/        page filters (RequireWalletPageFilter)
  Models/         Supabase.Postgrest.BaseModel DTOs
  Pages/          Account/, Traders/, Index, Privacy, Error
  Services/
    Polymarket/   public-data clients (PolymarketClient, PolygonUsdcClient, TraderStatsService)
    Secrets/      IFollowerSecretStore (DP-encrypted credentials store)
    Watcher/      TraderWatcher (background) + WatcherSupabase (service-role client)
    Executor/     OrderExecutor (background) + PolymarketClobClient + PolymarketOrderSigner +
                  PolymarketOrderAmounts + PolymarketHmacAuth + ExecutorSecretReader
    SupabaseSessionRefreshMiddleware.cs
    WalletInput.cs
  Program.cs      DI, security headers, rate limits
PolymtkCp.Tests/  xUnit (28 tests)
supabase/migrations/  raw SQL, append-only
```

## What is implemented

- **Auth**: register / login / logout / forgot+reset password backed by Supabase. Cookie scheme `"Cookies"` keyed off the Supabase JWT. `SupabaseSessionRefreshMiddleware` refreshes the access token a couple of minutes before expiry and re-issues the auth cookie. Per-request `Supabase.Client` injected via DI; user JWT passed through `SupabaseOptions.Headers["Authorization"]` so RLS works for every read AND write.
- **Profile + balance**: `/Account/Profile` connects/updates the wallet and stores L2 CLOB credentials + wallet private key (DP-encrypted, append-only, versioned). `BalancePillViewComponent` shows Portfolio = on-chain USDC.e (`eth_call balanceOf` to Polygon RPC) + Polymarket positions value, in parallel with 60s in-memory cache.
- **Traders**: `/Traders` (Index/Add/Edit/Detail/Leaderboard). `traders` rows are shared across Followers; `copy_plans` rows are per-Follower. `RequireWalletPageFilter` redirects wallet-less users to Profile.
- **Watcher** (`TraderWatcher`, IHostedService): polls every active CopyPlan's Trader on `/activity?user=...`, scales each new fill, enforces daily ops/money limits and expiry, writes `copy_trade_executions`. Paper plans → `mode='paper', status='simulated'|'skipped'`. Real plans → `mode='real', status='pending'`.
- **Executor** (`OrderExecutor`, IHostedService): pulls `mode='real', status='pending'` rows in FIFO order, decrypts the Follower's credentials, asks the CLOB for tick-size + neg-risk flag, builds the order with `PolymarketOrderAmounts` (USDC 6dp, shares 2dp, price snapped to tick), EIP-712-signs it via `PolymarketOrderSigner`, posts to `/order` with `PolymarketHmacAuth` headers, and transitions the row to `submitted`/`failed` with the CLOB id or reason.
- **Security baseline**: HSTS, HTTPS redirect, anti-forgery, HttpOnly+SameSite=Lax+Secure auth cookie (7-day sliding), rate-limit (10 req / 5 min per IP) on auth/profile endpoints, security headers including a Content-Security-Policy, Data Protection keys encrypted with a Key Vault KEK in prod.
- **Tests + CI**: 28 xUnit tests cover signing, HMAC headers, USDC/share rounding, wallet input parsing. GitHub Actions runs `dotnet build -c Release && dotnet test -c Release --no-build` before publishing.

## Database

| Table | Purpose | RLS |
|---|---|---|
| `traders` | Shared cache of public Polymarket wallets | All authenticated users SELECT/INSERT/UPDATE; no DELETE |
| `copy_plans` | Per-(Follower, Trader) configuration | Own-row via `auth.uid() = follower_id` |
| `follower_profiles` | One row per Follower; `polymarket_wallet_address` | Own-row |
| `follower_secrets` | Append-only versioned encrypted L2 + private key | Own-row select/insert/update; no DELETE (audit) |
| `copy_trade_executions` | Append-only copy-trade log | Own-row select/insert/update; no DELETE |

Every table has `created_at`/`updated_at` maintained by the `set_updated_at()` trigger. Indexes on FKs and `created_at desc`.

## Hard rules

1. **Never commit secrets.** Use User Secrets (dev) or App Service config (prod). `appsettings*.json` ships only placeholders. The Polymarket private key, L2 secret, and L2 passphrase must never be logged or returned to the browser.
2. **Never edit a pushed migration.** Add a new file in `supabase/migrations/<timestamp>_<name>.sql`. Apply with `npx supabase db push`.
3. **Never bypass RLS in request handlers.** Use the per-request scoped `Supabase.Client`. The service-role client (`WatcherSupabase`) is injected only into background services.
4. **Never trust the Supabase SDK boolean filter.** It silently no-ops on some columns. For boolean predicates, fetch the candidate rows (with a small `Limit` and a sane order) and filter client-side. See `FollowerSecretStore.GetStatusAsync`.
5. **Never widen logging around credentials.** `OrderExecutor` and `PolymarketClobClient` log order ids and CLOB error codes only — not request bodies, signatures, or headers.
6. **Mirror gating logic across pages.** When credential / wallet gating changes (e.g. enabling Real mode), update both `Pages/Traders/Add.cshtml(.cs)` and `Pages/Traders/Edit.cshtml(.cs)` — the Add page does not inherit from Edit.
7. **Keep DataProtection 10.0.5 pinned** in `PolymtkCp.csproj` until the Azure DP NuGets bump their transitive `System.Security.Cryptography.Xml` floor past 8.0.x. Re-check with `dotnet list package --vulnerable --include-transitive`.

## Workflow

```powershell
dotnet build
dotnet test
dotnet run --project PolymtkCp/PolymtkCp.csproj
```

Test the executor without spending money: keep CopyPlan in **Paper** mode and inspect `/Traders/{id}` history. To smoke-test Real mode, save credentials, flip one plan to Real with a tiny fixed-USDC amount, and watch the App Service log stream.

## Out of scope

- Limit-order mirroring (we only place GTC at mid).
- Real-time websocket streams (we poll on an interval).
- Mobile app.
