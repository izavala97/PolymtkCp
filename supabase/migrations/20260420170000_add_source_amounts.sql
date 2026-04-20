-- =============================================================================
-- copy_trade_executions: store source trade size/price
-- =============================================================================
-- The current columns (price, size_shares, size_usdc) hold the *copy's* sizing
-- after the plan's sizing rules are applied. That throws away the trader's
-- original amounts, which we need to:
--   - reconstruct real P&L once phase-2 real trading lands,
--   - retroactively fix sizing asymmetries (e.g. accumulate-then-exit
--     unbalances under fixed sizing + grouping),
--   - backtest alternative sizing strategies without re-fetching /activity.
-- Nullable because historical rows were written before this column existed.
-- =============================================================================

alter table public.copy_trade_executions
    add column if not exists source_price numeric,
    add column if not exists source_size_shares numeric,
    add column if not exists source_size_usdc numeric;
