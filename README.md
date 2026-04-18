# Polymarket Copy-Trading Platform

A platform that lets a **Follower** mirror the trades of a **Trader** on Polymarket.

## Tech stack

- ASP.NET Core Razor Pages (.NET 10)
- Bootstrap 5
- Supabase (Auth + Postgres for app data; **no Polymarket secrets stored**)
- Polymarket Gamma / Data / CLOB APIs (read-only today)
- Polygon JSON-RPC (read-only USDC.e balance for the "Cash" pill)

## Glossary

| Term | Meaning |
|------|---------|
| **Follower** | Logged-in user of this app whose account places copied trades |
| **Trader** | Public Polymarket trader whose activity the Follower copies |
| **CopyPlan** | A Follower's per-Trader configuration (sizing, daily limits, expiry) |
| **CopyTradeExecution** | Append-only record of one copy-trade decision (currently always `mode='paper'`) |
| **Position** | Outcome shares (YES or NO) currently held in a market |
| **Activity** | Time-ordered list of buys/sells on a wallet (public via Data API) |

## Project phases

The app is built in two phases. The current codebase implements **phase 1**.

| Phase | What | Server holds key? |
|-------|------|------------------|
| **1. Watcher + dashboard** (current) | Reads Traders via public Polymarket APIs, computes proposed copy-trades, shows them in a Razor dashboard. **Read-only against Polymarket** | No |
| **2. Auto-execution** (future) | Server places real orders on the Follower's account. Includes encrypted private-key storage and order placement | Yes — encrypted at rest |

What the app persists in Supabase per Follower:
- Email + auth (Supabase Auth)
- Polymarket proxy wallet address (public info)
- List of Traders being copied + per-Trader copy settings
- History of proposed copy-trades (and, in phase 2, executed ones)

What the app does **not** persist (current phase):
- Anything related to the Follower's Polymarket credentials

## What is implemented today

### Auth
- Email/password registration, login, logout, forgot/reset password — all backed by Supabase Auth
- Cookie-based ASP.NET auth scheme `"Cookies"` keyed off the Supabase JWT
- **`SupabaseSessionRefreshMiddleware`** transparently refreshes the access token a couple of minutes before expiry by hitting `{supabase_url}/auth/v1/token?grant_type=refresh_token` with the stored refresh token, and re-issues the auth cookie. This avoids the `PGRST303 JWT expired` errors that would otherwise hit any read/write after ~1 hour
- Per-request `Supabase.Client` factory: a fresh client is built per request and the user's JWT is passed via `SupabaseOptions.Headers["Authorization"]`, so RLS (`auth.uid()`) works for every read AND write through the SDK. This is the documented server-side pattern from the Supabase C# wiki

### Profile + balance display
- `/Account/Profile` lets the Follower connect or update their Polymarket wallet
- A `RequireWalletPageFilter` redirects authenticated users to `/Account/Profile` whenever they try to use a feature that needs a wallet (Option B — wallet required)
- Profile and the navbar pill show:
  - **Portfolio** = Cash + Positions value
  - **Cash** — on-chain USDC.e (`0x2791…84174`, 6 decimals) balance of the proxy wallet, fetched via `eth_call` to a public Polygon RPC (`https://1rpc.io/matic` by default; override via `Polygon:RpcUrl`)
  - **Positions value** — Polymarket Data API `/value?user=...`
- Both fetches run in parallel with per-wallet 60s in-memory caching. Partial failure is tolerated (e.g. positions show even if RPC is down)

### Traders dashboard
- `/Traders` — list of Traders the Follower is copying with sizing, daily limits, expiry, status, View / Remove
- `/Traders/Add` — add a Trader by wallet address or Polymarket profile URL; configure sizing (fixed USDC or % of notional), daily ops limit, daily money limit, expiry. The `traders` row is shared across all Followers; the per-Follower `copy_plans` row is unique
- `/Traders/{id}` — Trader detail with copy-plan summary, copy-trade history, current open positions, and recent on-chain activity. Wallets and event slugs deep-link to `polymarket.com/profile/{wallet}` and `polymarket.com/event/{slug}`

### Copy-trade executions
- Schema is in place for the watcher to write to: `mode` ∈ {paper, real}, `status` ∈ {simulated, skipped, pending, submitted, filled, failed}
- Detail page renders execution history with mode/status/side badges, reason text, and event links
- The watcher itself is the next milestone — see [Roadmap](#roadmap)

## Architecture

```
PolymtkCp/
  Components/
    BalancePillViewComponent.cs       # navbar Portfolio / Cash / Positions pill
  Filters/
    RequireWalletPageFilter.cs        # redirect to Profile if wallet missing
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
    SupabaseSessionRefreshMiddleware.cs  # JWT refresh before queries run
    WalletInput.cs                    # parse + normalize wallet input
  Program.cs                          # DI, auth, middleware pipeline
supabase/
  migrations/
    20260418060000_initial_schema.sql # consolidated initial schema
  reset-dev.sql                       # DEV-ONLY: drops app tables + migration history
```

### Database (Supabase Postgres)

Schema lives in `supabase/migrations/` as raw SQL (no EF Core). Today everything is in a single consolidated initial migration: `supabase/migrations/20260418060000_initial_schema.sql`.

| Table | Purpose | RLS |
|-------|---------|-----|
| `traders` | Shared cache of public Polymarket wallets (one row per wallet) | All authenticated users can SELECT/INSERT/UPDATE; no DELETE |
| `copy_plans` | Per-(Follower, Trader) copy configuration | Standard own-row via `auth.uid() = follower_id` |
| `follower_profiles` | One row per Follower; holds `polymarket_wallet_address`. `encrypted_api_key` is reserved for phase 2 | Standard own-row via `auth.uid() = follower_id` |
| `copy_trade_executions` | Append-only log of copy-trade decisions; `event_title`/`outcome`/`slug` are denormalized for fast rendering | Own-row SELECT/INSERT/UPDATE; no DELETE |

All tables have `created_at`/`updated_at`; the `set_updated_at()` trigger maintains `updated_at` on every UPDATE. Indexes are added on FK columns and `created_at desc` for the executions log.

### External APIs

| API | Purpose | Auth |
|-----|---------|------|
| `https://gamma-api.polymarket.com` | Market and event metadata | Public |
| `https://data-api.polymarket.com` | `/positions`, `/activity`, `/value` for any wallet | Public |
| `https://clob.polymarket.com` | *(phase 2)* place / cancel orders | L1 + L2 |
| `https://1rpc.io/matic` (or any Polygon RPC) | `eth_call` USDC.e `balanceOf(wallet)` for Cash | Public |

## Copy-trading semantics

### Configure a Trader

| Setting | Description |
|---------|-------------|
| **Sizing** | Either *fixed amount* per trade (e.g. $1) or *percentage* of the Trader's notional |
| **Daily ops limit** | Max number of copy-trades per UTC day |
| **Daily money limit** | Max USDC spent on copy-trades per UTC day |
| **Expires** | Optional expiry; plan stops emitting copy-trades after this timestamp |
| **Active** | Master on/off switch |

### Order semantics (phase 2)

We will always place **market-equivalent** orders (FAK limit at a price that crosses the book). We will not mirror resting limit orders.

- Trivial UX — Follower picks a dollar amount, that's it
- Fills immediately or not at all, no orders left hanging
- Slightly worse fill than a patient limit order (negligible at small sizes)
- Cannot replicate complex strategies (laddering, hedging) — out of scope

### Position lifecycle

| State | Trigger | Follower action |
|-------|---------|----------------|
| **Open** | Trader buys outcome shares | Market-buy same outcome for the configured size |
| **Closed by Trader** | Trader sells before market resolves | Market-sell Follower's full position in that outcome |
| **Resolved** | Market end date reached | No action — winning side auto-settles to 100¢ on-chain |

A "Won" badge on a Polymarket profile means the Trader sold profitably — **not** that the underlying market is over.

## Roadmap

### Next milestone — the watcher (still phase 1)

Build an `IHostedService` that:
1. Polls each active `copy_plans.trader_id`'s `/activity?user=...` endpoint on an interval (e.g. every 30s)
2. Diffs against the last seen `transactionHash` for that plan
3. For each new trade, scales it per the plan (fixed amount or %), enforces daily limits and expiry, and writes a `copy_trade_executions` row with `mode='paper'` and `status='simulated'` or `status='skipped'`
4. Surfaces results in the existing Detail page history table

### Phase 2 — auto-execution

- Encrypted private-key storage (ASP.NET Data Protection + Azure Key Vault for prod, DPAPI for dev)
- Order-placement service that signs and submits trades to the Polymarket CLOB
- Live PnL dashboard, kill-switches, per-Follower trade audit log

## Out of scope (for now)

- Limit-order mirroring
- Real-time websocket updates (we poll on an interval)
- Mobile app
# Polymarket Copy-Trading Platform

A platform that lets a **Follower** mirror the trades of a **Trader** on Polymarket.

## Tech stack

- ASP.NET Core (Razor Pages)
- Bootstrap 5
- Supabase (Auth + Postgres for app data; **no Polymarket secrets stored**)
- Polymarket Gamma / Data / CLOB APIs

## Glossary

| Term | Meaning |
|------|---------|
| **Follower** | Logged-in user of this app whose account places copied trades |
| **Trader** | Public Polymarket trader whose activity the Follower copies |
| **Position** | Outcome shares (YES or NO) currently held in a market |
| **Activity** | Time-ordered list of buys/sells on a wallet (public via Data API) |

## Core features

### 1. Architecture

The app is built in two phases. The current codebase only does **phase 1**.

| Phase | What | Server holds key? |
|-------|------|------------------|
| **1. Watcher + dashboard** (current) | Polls Traders via public Polymarket APIs, computes proposed copy-trades, shows them in a Razor dashboard. **Read-only against Polymarket** | No — nothing to hold |
| **2. Auto-execution** (future) | Server places real orders on the Follower's account, including while the user is offline | Yes — encrypted at rest, decrypted in memory only when placing an order |

Auto-trading inherently requires the server to act on the user's behalf 24/7, so the key cannot live in the user's browser. Phase 2 will store private keys encrypted with ASP.NET Data Protection (master key in Azure Key Vault for prod, DPAPI for dev), with strict access logging and per-Follower Row-Level Security in Supabase. This puts the app in the same trust bucket as a centralized exchange or a trading bot service — which it is.

What the app persists in Supabase per Follower:
- Email + auth (Supabase Auth)
- Funder (proxy wallet) address — public info
- List of Traders being copied + per-Trader copy settings
- History of proposed copy-trades (and, in phase 2, executed ones)

What the app does **not** persist (current phase):
- Anything related to the Follower's Polymarket credentials

## Project phases

**Phase 1 (current scope) — public APIs only, no key handling:**

1. Watcher service that polls a Trader's positions/activity and detects new buys/sells
2. Dashboard for the Follower to add Traders, configure copy settings, and review proposed trades
3. "Paper trading" log — every proposed trade is recorded, nothing is sent to Polymarket

**Phase 2 (future) — auto-execution:**

4. Encrypted private-key storage (ASP.NET Data Protection + Azure Key Vault)
5. Order-placement service that signs and submits trades to the Polymarket CLOB on behalf of the Follower
6. Live PnL dashboard, kill-switches, and per-Follower trade audit log

### 2. Copy a Trader

The Follower provides a Trader's wallet address (or Polymarket profile URL) and configures:

| Setting | Description |
|---------|-------------|
| **Sizing** | Either a *fixed amount* per trade (e.g. $1) or a *percentage* of the Trader's notional |
| **Daily trade limit (N)** | Max number of copy-trades per day to cap risk |
| **Direction-only mode** | Copy YES/NO direction at market price; ignore the Trader's exact price/size strategy |

### 3. Order semantics (simplified)

We always place **market-equivalent** orders (FAK limit at a price that crosses the book). We do **not** mirror resting limit orders. Tradeoffs:

- Trivial UX — Follower picks a dollar amount, that's it
- Fills immediately or not at all, no orders left hanging
- Slightly worse fill than a patient limit order (negligible at small sizes)
- Cannot replicate complex strategies (laddering, hedging) — out of scope

### 4. Position lifecycle

A Polymarket position goes through three states. The copy engine reacts to each:

| State | Trigger | Follower action |
|-------|---------|----------------|
| **Open** | Trader buys outcome shares | Market-buy same outcome for $N |
| **Closed by Trader** | Trader sells before market resolves | Market-sell Follower's full position in that outcome |
| **Resolved** | Market end date reached | No action — winning side auto-settles to 100¢ on-chain |

Note: a "Won" badge on a Polymarket profile means the Trader sold profitably — **not** that the underlying market is over.

## Polymarket API surface used

**Phase 1 (server) — public, no auth:**

| Endpoint | Purpose |
|----------|---------|
| `GET https://gamma-api.polymarket.com/markets` | Market metadata, tick size, neg-risk flag |
| `GET https://data-api.polymarket.com/positions?user={addr}` | Trader's current positions |
| `GET https://data-api.polymarket.com/activity?user={addr}` | Trader's trade history (for diff polling) |

**Phase 2 (server, future):**

| Endpoint | Purpose | Auth |
|----------|---------|------|
| `POST https://clob.polymarket.com/...` | Place / cancel orders for the Follower | L1 (private key) + L2 (derived) |

## Out of scope (for now)

- Limit-order mirroring
- Many Traders per Follower (cap to a small N initially)
- Real-time websocket updates (we poll on an interval)
- Mobile app