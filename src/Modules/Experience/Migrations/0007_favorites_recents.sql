-- Spec 008 R2 T019 (FR-017, US4 scenario 6): a person's favorites and recent visits. Both are the person's own and
-- hold only an address or an item reference with a label; what they point to is checked again on every read.
create table experience.favorite (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  kind text not null check (kind in ('page', 'item')),
  path text null check (path is null or (path ~ '^/[a-z0-9/_-]*$' and length(path) <= 300)),
  source text null,
  item_id uuid null,
  link text null,
  label text not null check (length(label) between 1 and 200),
  position int not null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  check ((kind = 'page' and path is not null and source is null and item_id is null)
      or (kind = 'item' and path is null and source is not null and item_id is not null and link is not null))
);
create unique index favorite_page on experience.favorite (tenant_id, user_id, path) where kind = 'page';
create unique index favorite_item on experience.favorite (tenant_id, user_id, source, item_id) where kind = 'item';
create index favorite_user on experience.favorite (tenant_id, user_id, position);
select platform.enable_tenant_rls('experience.favorite');
grant select, insert, update, delete on experience.favorite to tb_app;

create table experience.recent_visit (
  tenant_id uuid not null,
  user_id uuid not null,
  path text not null check (path ~ '^/[A-Za-z0-9/_.-]*$' and length(path) <= 300),
  label text not null check (length(label) between 1 and 200),
  visited_at timestamptz not null,
  primary key (tenant_id, user_id, path)
);
create index recent_visit_user on experience.recent_visit (tenant_id, user_id, visited_at desc);
select platform.enable_tenant_rls('experience.recent_visit');
grant select, insert, update, delete on experience.recent_visit to tb_app;
