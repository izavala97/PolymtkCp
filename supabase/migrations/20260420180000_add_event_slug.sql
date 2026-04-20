-- =============================================================================
-- copy_trade_executions: persist parent event slug
-- =============================================================================
-- The Polymarket activity API returns both `slug` (per-market) and `eventSlug`
-- (parent event). The canonical user-facing URL is `/event/{eventSlug}`, so we
-- need to store the eventSlug to produce working deep-links from the UI.
-- Nullable because historical rows were written before this column existed.
-- =============================================================================

alter table public.copy_trade_executions
    add column if not exists event_slug text;
