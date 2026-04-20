-- =============================================================================
-- Initial schema (consolidated)
-- =============================================================================
-- Tables:
--   * traders                — shared cache of public Polymarket wallets
--   * copy_plans             — per-Follower configuration for copying a Trader
--   * follower_profiles      — per-Follower Polymarket account info (wallet, etc.)
--   * copy_trade_executions  — per-Follower record of copy-trade decisions
--
-- Domain glossary:
--   Follower    — logged-in user of this app
--   Trader      — public Polymarket wallet being copied
--   CopyPlan    — Follower's per-Trader configuration
-- =============================================================================


-- -----------------------------------------------------------------------------
-- Helper: trigger that maintains updated_at on row mutation.
-- -----------------------------------------------------------------------------
create or replace function public.set_updated_at()
returns trigger
language plpgsql
as $$
begin
    new.updated_at := now();
    return new;
end;
$$;


-- =============================================================================
-- traders (shared)
-- =============================================================================
create table if not exists public.traders (
    id              uuid primary key default gen_random_uuid(),
    wallet_address  text not null unique,
    nickname        text,
    created_at      timestamptz not null default now(),
    updated_at      timestamptz not null default now()
);

drop trigger if exists traders_set_updated_at on public.traders;
create trigger traders_set_updated_at
    before update on public.traders
    for each row execute function public.set_updated_at();

alter table public.traders enable row level security;

drop policy if exists "traders_select_authenticated" on public.traders;
create policy "traders_select_authenticated"
    on public.traders for select
    to authenticated
    using (true);

drop policy if exists "traders_insert_authenticated" on public.traders;
create policy "traders_insert_authenticated"
    on public.traders for insert
    to authenticated
    with check (true);

drop policy if exists "traders_update_authenticated" on public.traders;
create policy "traders_update_authenticated"
    on public.traders for update
    to authenticated
    using (true)
    with check (true);

-- Deliberately no DELETE policy — traders are append-only from the app's
-- perspective. Cleanup of orphans is a future maintenance task.


-- =============================================================================
-- copy_plans (per Follower)
-- =============================================================================
create table if not exists public.copy_plans (
    id                              uuid primary key default gen_random_uuid(),
    follower_id                     uuid not null references auth.users(id) on delete cascade,
    trader_id                       uuid not null references public.traders(id) on delete cascade,

    -- 'paper' = simulated only (phase 1 / opt-in safety net).
    -- 'real'  = phase 2 will submit orders to the CLOB on the Follower's behalf.
    mode                            text not null default 'paper'
                                        check (mode in ('paper', 'real')),

    sizing_mode                     text not null default 'fixed'
                                        check (sizing_mode in ('fixed', 'percent')),
    fixed_amount_usd                numeric(12, 4),
    percent_of_notional             numeric(5, 2),

    -- Daily limits. Either, both, or neither may be set.
    -- If set, copying pauses for the rest of the UTC day once the limit is hit.
    daily_trade_operations_limit    int,
    daily_trade_money_limit         numeric(12, 4),

    expires_at                      timestamptz,
    is_active                       boolean not null default true,

    created_at                      timestamptz not null default now(),
    updated_at                      timestamptz not null default now(),

    unique (follower_id, trader_id),

    constraint copy_plans_sizing_amount_present check (
        (sizing_mode = 'fixed'   and fixed_amount_usd    is not null and fixed_amount_usd    > 0)
     or (sizing_mode = 'percent' and percent_of_notional is not null and percent_of_notional > 0)
    ),
    constraint copy_plans_operations_limit_positive check (
        daily_trade_operations_limit is null or daily_trade_operations_limit > 0
    ),
    constraint copy_plans_money_limit_positive check (
        daily_trade_money_limit is null or daily_trade_money_limit > 0
    )
);

create index if not exists copy_plans_follower_id_idx on public.copy_plans (follower_id);
create index if not exists copy_plans_trader_id_idx   on public.copy_plans (trader_id);

drop trigger if exists copy_plans_set_updated_at on public.copy_plans;
create trigger copy_plans_set_updated_at
    before update on public.copy_plans
    for each row execute function public.set_updated_at();

alter table public.copy_plans enable row level security;

drop policy if exists "copy_plans_select_own" on public.copy_plans;
create policy "copy_plans_select_own"
    on public.copy_plans for select
    to authenticated
    using (auth.uid() = follower_id);

drop policy if exists "copy_plans_insert_own" on public.copy_plans;
create policy "copy_plans_insert_own"
    on public.copy_plans for insert
    to authenticated
    with check (auth.uid() = follower_id);

drop policy if exists "copy_plans_update_own" on public.copy_plans;
create policy "copy_plans_update_own"
    on public.copy_plans for update
    to authenticated
    using (auth.uid() = follower_id)
    with check (auth.uid() = follower_id);

drop policy if exists "copy_plans_delete_own" on public.copy_plans;
create policy "copy_plans_delete_own"
    on public.copy_plans for delete
    to authenticated
    using (auth.uid() = follower_id);


-- =============================================================================
-- follower_profiles (per Follower)
-- =============================================================================
-- One row per Follower, created on demand the first time they save their
-- Polymarket wallet. Phase 1 only stores the public wallet address. The
-- encrypted_api_key column is reserved for phase 2 (server-side trade
-- execution) and is unused by the current app.
create table if not exists public.follower_profiles (
    follower_id                 uuid primary key references auth.users(id) on delete cascade,
    polymarket_wallet_address   text,
    encrypted_api_key           text,                       -- phase 2; stays null today
    created_at                  timestamptz not null default now(),
    updated_at                  timestamptz not null default now()
);

drop trigger if exists follower_profiles_set_updated_at on public.follower_profiles;
create trigger follower_profiles_set_updated_at
    before update on public.follower_profiles
    for each row execute function public.set_updated_at();

alter table public.follower_profiles enable row level security;

drop policy if exists "follower_profiles_select_own" on public.follower_profiles;
create policy "follower_profiles_select_own"
    on public.follower_profiles for select
    to authenticated
    using (auth.uid() = follower_id);

drop policy if exists "follower_profiles_insert_own" on public.follower_profiles;
create policy "follower_profiles_insert_own"
    on public.follower_profiles for insert
    to authenticated
    with check (auth.uid() = follower_id);

drop policy if exists "follower_profiles_update_own" on public.follower_profiles;
create policy "follower_profiles_update_own"
    on public.follower_profiles for update
    to authenticated
    using (auth.uid() = follower_id)
    with check (auth.uid() = follower_id);

drop policy if exists "follower_profiles_delete_own" on public.follower_profiles;
create policy "follower_profiles_delete_own"
    on public.follower_profiles for delete
    to authenticated
    using (auth.uid() = follower_id);


-- =============================================================================
-- copy_trade_executions (per Follower)
-- =============================================================================
-- Written by the watcher when it detects a Trader making a trade. Each row
-- represents one decision for one (CopyPlan, source trade) pair. In phase 1
-- every row is mode='paper' (no real order is placed). In phase 2, the
-- executor will write rows with mode='real' and update status as the order
-- progresses on the CLOB.
create table if not exists public.copy_trade_executions (
    id                          uuid primary key default gen_random_uuid(),
    copy_plan_id                uuid not null references public.copy_plans(id) on delete cascade,

    -- Denormalized for RLS without joins. Must always equal the parent plan's follower_id.
    follower_id                 uuid not null references auth.users(id) on delete cascade,

    -- 'paper' = simulated only (phase 1). 'real' = submitted to CLOB (phase 2).
    mode                        text not null default 'paper'
                                    check (mode in ('paper', 'real')),

    -- Lifecycle:
    --   simulated : recorded as a paper trade only
    --   skipped   : a daily limit / expiration / inactive plan blocked it
    --   pending   : (phase 2) order created but not yet submitted
    --   submitted : (phase 2) submitted to CLOB
    --   filled    : (phase 2) fully or partially filled
    --   failed    : execution attempt failed
    status                      text not null
                                    check (status in (
                                        'simulated', 'skipped',
                                        'pending', 'submitted', 'filled', 'failed'
                                    )),

    -- Source trade on the watched Trader's wallet. The activity hash from the
    -- Polymarket /activity feed is the natural dedup key.
    source_activity_hash        text not null,
    source_timestamp            timestamptz not null,

    -- Polymarket identifiers
    asset                       text not null,        -- CLOB token id
    condition_id                text,
    side                        text not null
                                    check (side in ('BUY', 'SELL')),

    -- Trade economics for this copy-trade (already scaled by the CopyPlan sizing).
    price                       numeric(10, 6) not null,
    size_shares                 numeric(20, 6) not null,
    size_usdc                   numeric(14, 4) not null,

    -- Cached display fields (avoid joining Polymarket on every render).
    event_title                 text,
    outcome                     text,
    slug                        text,

    -- Reason for skipped / failed.
    reason                      text,

    -- Set when the order was actually placed (phase 2). Null for paper trades.
    executed_at                 timestamptz,

    created_at                  timestamptz not null default now(),
    updated_at                  timestamptz not null default now(),

    -- Idempotency: never emit two copies of the same source trade for the same plan.
    unique (copy_plan_id, source_activity_hash)
);

create index if not exists copy_trade_executions_follower_id_idx
    on public.copy_trade_executions (follower_id);
create index if not exists copy_trade_executions_copy_plan_id_idx
    on public.copy_trade_executions (copy_plan_id);
create index if not exists copy_trade_executions_created_at_idx
    on public.copy_trade_executions (created_at desc);

drop trigger if exists copy_trade_executions_set_updated_at on public.copy_trade_executions;
create trigger copy_trade_executions_set_updated_at
    before update on public.copy_trade_executions
    for each row execute function public.set_updated_at();

alter table public.copy_trade_executions enable row level security;

drop policy if exists "copy_trade_executions_select_own" on public.copy_trade_executions;
create policy "copy_trade_executions_select_own"
    on public.copy_trade_executions for select
    to authenticated
    using (auth.uid() = follower_id);

drop policy if exists "copy_trade_executions_insert_own" on public.copy_trade_executions;
create policy "copy_trade_executions_insert_own"
    on public.copy_trade_executions for insert
    to authenticated
    with check (auth.uid() = follower_id);

drop policy if exists "copy_trade_executions_update_own" on public.copy_trade_executions;
create policy "copy_trade_executions_update_own"
    on public.copy_trade_executions for update
    to authenticated
    using (auth.uid() = follower_id)
    with check (auth.uid() = follower_id);

-- No DELETE policy — execution history is append-only from the app's perspective.
