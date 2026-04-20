-- =============================================================================
-- Phase-2 prep: per-Follower Polymarket L2 API credentials
-- =============================================================================
-- Stores ASP.NET Data Protection-encrypted JSON {api_key, secret, passphrase}.
-- The KEK lives in Azure Key Vault; the Data Protection key ring is persisted
-- to Azure Blob Storage. See PolymtkCp/Services/Secrets/FollowerSecretStore.cs.
--
-- Versioned + audited: rather than overwriting in place, every Set creates a
-- new row with version+1 and flips the previous row's is_active=false. We
-- never DELETE from the app (RLS has no DELETE policy) so the history stays
-- intact for incident review.
--
-- Drops the unused follower_profiles.encrypted_api_key column that was
-- reserved during phase 1 and never written.
-- =============================================================================

alter table public.follower_profiles
    drop column if exists encrypted_api_key;

create table if not exists public.follower_secrets (
    id              uuid primary key default gen_random_uuid(),
    follower_id     uuid not null references auth.users(id) on delete cascade,
    version         int  not null,
    is_active       boolean not null default true,
    ciphertext      text not null,
    created_at      timestamptz not null default now(),
    updated_at      timestamptz not null default now(),
    unique (follower_id, version)
);

comment on table public.follower_secrets is
    'Encrypted Polymarket L2 API credentials. ciphertext is ASP.NET Data Protection output (base64). KEK in Azure Key Vault.';

-- At most one active row per follower.
create unique index if not exists follower_secrets_one_active_per_follower
    on public.follower_secrets (follower_id)
    where is_active;

create index if not exists follower_secrets_follower_id_idx
    on public.follower_secrets (follower_id);

drop trigger if exists follower_secrets_set_updated_at on public.follower_secrets;
create trigger follower_secrets_set_updated_at
    before update on public.follower_secrets
    for each row execute function public.set_updated_at();

alter table public.follower_secrets enable row level security;

drop policy if exists "follower_secrets_select_own" on public.follower_secrets;
create policy "follower_secrets_select_own"
    on public.follower_secrets for select
    to authenticated
    using (auth.uid() = follower_id);

drop policy if exists "follower_secrets_insert_own" on public.follower_secrets;
create policy "follower_secrets_insert_own"
    on public.follower_secrets for insert
    to authenticated
    with check (auth.uid() = follower_id);

drop policy if exists "follower_secrets_update_own" on public.follower_secrets;
create policy "follower_secrets_update_own"
    on public.follower_secrets for update
    to authenticated
    using (auth.uid() = follower_id)
    with check (auth.uid() = follower_id);

-- No DELETE policy on purpose: rows are append-only audit history.
