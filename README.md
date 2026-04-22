# Polymarket Copy-Trading Platform

ASP.NET Core Razor Pages app (.NET 10) that lets a **Follower** mirror the trades of a public **Trader** on Polymarket. Live: <https://polymtkcp.azurewebsites.net>.

## What it does

- **Auth** — Supabase email/password with cookie sessions and silent JWT refresh.
- **Profile + balance pill** — connect a Polymarket wallet and see Portfolio = on-chain USDC.e balance (Polygon RPC) + market value of open positions (Polymarket Data API).
- **Traders dashboard** — add/edit/remove Traders to copy. Per-Trader settings: sizing (fixed USDC or % of notional), daily ops/money limits, expiry, group-similar-fills.
- **Watcher** (background service) — polls every active CopyPlan's Trader on `data-api.polymarket.com/activity`, scales each new fill, enforces limits, writes a `copy_trade_executions` row.
- **Executor** (background service) — picks up `mode='real', status='pending'` rows, EIP-712-signs the order with the Follower's wallet key, submits it to the CLOB, and transitions the row to `submitted`/`failed`.

## Glossary

| Term | Meaning |
|---|---|
| **Follower** | Logged-in user of this app whose Polymarket account places copied trades |
| **Trader** | Public Polymarket trader being copied (one row per wallet, shared) |
| **CopyPlan** | A Follower's per-Trader configuration |
| **CopyTradeExecution** | Append-only row per copy-trade decision (paper or real) |

## Build & run

```powershell
dotnet build
dotnet run --project PolymtkCp/PolymtkCp.csproj
dotnet test                  # 28 tests covering signing, HMAC, amounts, wallet input
```

Dev URLs: <https://localhost:7164>, <http://localhost:5287>. Solution: `PolymtkCp.slnx`.

## Architecture

```
PolymtkCp/
  Components/    BalancePillViewComponent.cs   — navbar Portfolio pill
  Filters/       RequireWalletPageFilter.cs    — gate routes that need a wallet
  Models/        Supabase.Postgrest.BaseModel DTOs (Trader, CopyPlan, …)
  Pages/         Account/  Traders/  + Index, Privacy, Error
  Services/
    Polymarket/  PolymarketClient (data API), PolygonUsdcClient (Cash via eth_call),
                 TraderStatsService
    Secrets/     IFollowerSecretStore + FollowerSecretStore (DP-encrypted creds)
    Watcher/     TraderWatcher (IHostedService) + WatcherSupabase (service-role client)
    Executor/    OrderExecutor (IHostedService), PolymarketClobClient,
                 PolymarketOrderSigner (EIP-712), PolymarketOrderAmounts,
                 PolymarketHmacAuth (POLY_* headers), ExecutorSecretReader
    SupabaseSessionRefreshMiddleware.cs        — refresh JWT before queries
    WalletInput.cs                             — parse 0x address or profile URL
  Program.cs                                   — DI, security headers, rate limits
PolymtkCp.Tests/                               — xUnit
supabase/migrations/                           — raw SQL (no EF Core)
```

### Database (Supabase Postgres)

| Table | Purpose | RLS |
|---|---|---|
| `traders` | Shared cache, one row per public wallet | Authenticated SELECT/INSERT/UPDATE |
| `copy_plans` | Per-(Follower, Trader) configuration | Own-row via `auth.uid() = follower_id` |
| `follower_profiles` | One row per Follower; holds `polymarket_wallet_address` | Own-row |
| `follower_secrets` | Append-only versioned encrypted credentials (L2 triple + private key) | Own-row select/insert/update; no DELETE |
| `copy_trade_executions` | Append-only log of copy-trade decisions | Own-row select/insert/update; no DELETE |

All tables have `created_at`/`updated_at` (`set_updated_at()` trigger). Migrations are committed; never edit one after pushing.

## External APIs

| API | Used for | Auth |
|---|---|---|
| `gamma-api.polymarket.com` | Market / event metadata | Public |
| `data-api.polymarket.com` | `/positions`, `/activity`, `/value` | Public |
| `clob.polymarket.com` | Place orders (`/order`), tick-size + neg-risk lookup | L1 (EIP-712 private-key signature) + L2 (HMAC headers) |
| `1rpc.io/matic` (default) | `eth_call` USDC.e `balanceOf` for Cash | Public |

## Order semantics

- Orders are submitted as **GTC** at the live mid-price (signed via EIP-712 against the regular or neg-risk CTF Exchange contract on Polygon).
- Sizing is fixed-USDC or % of trader notional, rounded to the market's tick size (0.1 / 0.01 / 0.001 / 0.0001) and 2-decimal share precision. USDC has 6 decimals.
- Daily ops/money limits and plan expiry are enforced **before** the row is written.
- Real-mode rows that fail (signature, balance, tick-size, etc.) land in the executions log with a human-readable `reason`. They are not retried automatically.

## Conventions

- `nullable` and `implicit usings` enabled.
- All HTTP request handlers use the per-request `Supabase.Client` (DI scoped, JWT injected for RLS). The watcher + executor are the only consumers of the service-role client (`WatcherSupabase`).
- Secrets live in User Secrets (dev) or App Service / env vars (prod). Never hard-coded.
- Use `ILogger<T>` with structured templates. Never log the L2 secret, passphrase, or private key.
- New schema goes in a new `supabase/migrations/<timestamp>_<name>.sql` file. Apply with `npx supabase db push`.

See [DEPLOYMENT.md](DEPLOYMENT.md) for Azure / Supabase / GitHub Actions setup, and [AGENTS.md](AGENTS.md) for the in-repo agent contract.
