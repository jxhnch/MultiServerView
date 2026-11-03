/** Same content as docs/supabase.sql - in the UI so it can be copied directly. */
export const SUPABASE_SQL = `-- Run once in the Supabase SQL editor
create table if not exists public.vaults (
  user_id    uuid primary key default auth.uid() references auth.users (id) on delete cascade,
  version    bigint      not null default 1,
  blob       text        not null,
  updated_at timestamptz not null default now()
);

alter table public.vaults enable row level security;

create policy "vault_select_own" on public.vaults
  for select to authenticated using (auth.uid() = user_id);
create policy "vault_insert_own" on public.vaults
  for insert to authenticated with check (auth.uid() = user_id);
create policy "vault_update_own" on public.vaults
  for update to authenticated using (auth.uid() = user_id) with check (auth.uid() = user_id);
create policy "vault_delete_own" on public.vaults
  for delete to authenticated using (auth.uid() = user_id);
`;
