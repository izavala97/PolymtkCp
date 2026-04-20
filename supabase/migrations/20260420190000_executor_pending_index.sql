-- =============================================================================
-- Phase-2 executor: index for the executor's pending-row scan.
-- =============================================================================
-- The OrderExecutor IHostedService polls
--     SELECT ... FROM copy_trade_executions
--      WHERE mode = 'real' AND status = 'pending'
--      ORDER BY created_at
--      LIMIT N
-- on every tick. A partial index keyed by (created_at) and filtered to the
-- pending-real subset is the cheapest possible scan: it's nearly always near-
-- empty (rows leave it the moment they're submitted/failed), so the executor
-- pays for almost nothing per tick.
-- =============================================================================

create index if not exists copy_trade_executions_executor_pending_idx
    on public.copy_trade_executions (created_at)
    where mode = 'real' and status = 'pending';
