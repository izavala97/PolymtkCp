-- =============================================================================
-- DEV-ONLY: nuke the app schema and the migration history, then you can
-- re-apply migrations from a clean slate.
--
-- Usage:
--   1) Paste this into the Supabase Dashboard -> SQL Editor and run it.
--      (auth.users is NOT touched — your existing logins survive.)
--   2) From the repo root run:  supabase db push
--      That will apply supabase/migrations/20260418060000_initial_schema.sql
--      as if it were the first migration.
--
-- DO NOT run this in production.
-- =============================================================================

-- Drop app tables (cascade nukes triggers, policies, indexes, FKs).
drop table if exists public.copy_trade_executions cascade;
drop table if exists public.copy_plans            cascade;
drop table if exists public.follower_profiles     cascade;
drop table if exists public.traders               cascade;

-- Drop the shared updated_at trigger function.
drop function if exists public.set_updated_at() cascade;

-- Forget the old migration history so `supabase db push` reapplies the
-- consolidated initial schema.
delete from supabase_migrations.schema_migrations
where version in (
    '20260418030119',
    '20260418040000',
    '20260418050000'
);
