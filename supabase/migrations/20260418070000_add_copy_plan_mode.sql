-- =============================================================================
-- copy_plans: add per-plan mode (paper | real)
-- =============================================================================
-- Phase 1 the watcher always emits paper, regardless of this column. Phase 2's
-- executor will read plans where mode='real' and submit orders to the CLOB.
-- Defaulting to 'paper' so existing rows are safe.
-- =============================================================================

alter table public.copy_plans
    add column if not exists mode text not null default 'paper'
        check (mode in ('paper', 'real'));
