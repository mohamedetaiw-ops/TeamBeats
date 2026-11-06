-- Spec 008 (R1): personalization policy per page and region, the preference profile, personal layouts and audience
-- rules. Personal layouts are operations over the published page, so they survive releases and reset without touching
-- the page. Preferences hold no business data (FR-036).
create table experience.audience_rule (
  tenant_id uuid not null,
  rule_id uuid not null,
  labels jsonb not null,
  conditions jsonb not null,
  updated_by uuid null,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, rule_id)
);
select platform.enable_tenant_rls('experience.audience_rule');
grant select, insert, update on experience.audience_rule to tb_app;

-- A row overrides the default for one region: regions the template marks personal are delegated to everyone, others
-- are not personalizable. An empty rights list turns a default delegation off.
create table experience.personalization_policy (
  tenant_id uuid not null,
  page_id uuid not null,
  region_id text not null check (region_id ~ '^[a-z][a-z0-9-]{0,39}$'),
  audience_rule_id uuid null,
  rights text[] not null check (rights <@ array['add', 'hide', 'remove', 'reorder']::text[]),
  max_personal_placements int not null default 12 check (max_personal_placements between 0 and 24),
  updated_by uuid null,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, page_id, region_id),
  foreign key (tenant_id, page_id) references experience.page (tenant_id, id),
  foreign key (tenant_id, audience_rule_id) references experience.audience_rule (tenant_id, rule_id)
);
select platform.enable_tenant_rls('experience.personalization_policy');
grant select, insert, update, delete on experience.personalization_policy to tb_app;

create table experience.preference_profile (
  tenant_id uuid not null,
  user_id uuid not null,
  schema_version int not null,
  values jsonb not null,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, user_id)
);
select platform.enable_tenant_rls('experience.preference_profile');
grant select, insert, update, delete on experience.preference_profile to tb_app;

create table experience.personal_layout (
  tenant_id uuid not null,
  user_id uuid not null,
  page_id uuid not null,
  schema_version int not null,
  revision int not null,
  operations jsonb not null,
  updated_at timestamptz not null default now(),
  primary key (tenant_id, user_id, page_id),
  foreign key (tenant_id, page_id) references experience.page (tenant_id, id)
);
select platform.enable_tenant_rls('experience.personal_layout');
grant select, insert, update, delete on experience.personal_layout to tb_app;
