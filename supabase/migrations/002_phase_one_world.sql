-- Additive phase-one data. Existing characters remain in the field at their saved coordinates.
alter table public.game_characters add column if not exists map_id text not null default 'verdant-field';
alter table public.game_characters drop constraint if exists game_characters_map_id_check;
alter table public.game_characters add constraint game_characters_map_id_check check (map_id in ('lumen-town', 'verdant-field'));

alter table public.game_character_quests drop constraint if exists game_character_quests_status_check;
alter table public.game_character_quests add constraint game_character_quests_status_check
    check (status in ('active', 'complete', 'claimed'));

create table if not exists public.game_character_equipment (
    character_id uuid not null references public.game_characters(id) on delete cascade,
    slot text not null check (slot in ('weapon')),
    item_key text not null check (item_key in ('iron_sword', 'oak_staff')),
    updated_at timestamptz not null default now(),
    primary key (character_id, slot)
);

-- Give existing characters the same unequipped starter weapon as new characters.
insert into public.game_inventory (character_id, item_key, quantity)
select id, case when job = 'Mage' then 'oak_staff' else 'iron_sword' end, 1
from public.game_characters
on conflict (character_id, item_key) do nothing;

alter table public.game_character_equipment enable row level security;
revoke all on public.game_character_equipment from anon, authenticated;

comment on table public.game_character_equipment is 'Server-owned equipped items; no browser Data API access.';
