-- Core persistent state. The browser authenticates with Supabase Auth, but game tables are
-- only accessed by the trusted game server. No client-facing RLS policies are granted here.

create extension if not exists pgcrypto;

create table if not exists public.game_characters (
    id uuid primary key default gen_random_uuid(),
    owner_id uuid not null references auth.users(id) on delete cascade,
    name text not null check (char_length(name) between 3 and 18),
    name_key text generated always as (lower(name)) stored,
    job text not null check (job in ('Swordsman', 'Mage')),
    level integer not null default 1 check (level between 1 and 99),
    experience bigint not null default 0 check (experience >= 0),
    x integer not null default 7 check (x between 0 and 15),
    y integer not null default 8 check (y between 0 and 15),
    hp integer not null default 100 check (hp between 0 and 10000),
    mp integer not null default 40 check (mp between 0 and 10000),
    gold bigint not null default 0 check (gold >= 0),
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    unique (owner_id, name_key)
);

create index if not exists game_characters_owner_idx on public.game_characters(owner_id, created_at);

create table if not exists public.game_inventory (
    character_id uuid not null references public.game_characters(id) on delete cascade,
    item_key text not null check (char_length(item_key) between 1 and 80),
    quantity integer not null default 0 check (quantity >= 0),
    updated_at timestamptz not null default now(),
    primary key (character_id, item_key)
);

create table if not exists public.game_reward_receipts (
    reward_key text primary key,
    character_id uuid not null references public.game_characters(id) on delete cascade,
    reward_kind text not null,
    created_at timestamptz not null default now()
);

create table if not exists public.game_character_quests (
    character_id uuid not null references public.game_characters(id) on delete cascade,
    quest_key text not null,
    status text not null default 'active' check (status in ('active', 'complete')),
    progress integer not null default 0 check (progress >= 0),
    updated_at timestamptz not null default now(),
    primary key (character_id, quest_key)
);

alter table public.game_characters enable row level security;
alter table public.game_inventory enable row level security;
alter table public.game_reward_receipts enable row level security;
alter table public.game_character_quests enable row level security;

revoke all on public.game_characters, public.game_inventory,
    public.game_reward_receipts, public.game_character_quests from anon, authenticated;

comment on table public.game_characters is 'Server-owned character state; no browser Data API access.';
comment on table public.game_reward_receipts is 'Idempotency receipts for server-authorized reward transactions.';
