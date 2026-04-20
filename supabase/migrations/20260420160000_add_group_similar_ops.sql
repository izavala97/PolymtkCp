-- =============================================================================
-- copy_plans: add group_similar_ops
-- =============================================================================
-- Optional batch size N to collapse bursts of adjacent same-(asset, side) fills
-- into a single simulated copy-trade. When NULL, grouping is disabled and every
-- fill emits its own row (current behavior).
--
-- Example: trader splits one order into 10 partial fills on the same market
-- and side. With group_similar_ops = 5, the watcher emits 2 simulated rows
-- (one per chunk of 5); the 8 "extras" are still recorded as skipped with
-- reason='grouped', preserving the 1:1 source_activity_hash ↔ row dedup.
-- =============================================================================

alter table public.copy_plans
    add column if not exists group_similar_ops int
        check (group_similar_ops is null or group_similar_ops >= 2);
