-- Spec 008 R3 US6, FR-033: behavioral personalization is off unless the tenant turns it on, with each person's consent.
-- Signals are a person's own daily counts per address, kept 90 days, never read for anyone else.
create table experience.behavior_policy (
  tenant_id uuid not null primary key,
  enabled boolean not null default false,
  consent_mode text not null default 'opt_in' check (consent_mode in ('opt_in', 'opt_out')),
  updated_by uuid null,
  updated_at timestamptz not null
);
select platform.enable_tenant_rls('experience.behavior_policy');
grant select, insert, update on experience.behavior_policy to tb_app;

create table experience.behavior_consent (
  tenant_id uuid not null,
  user_id uuid not null,
  consented boolean not null,
  decided_at timestamptz not null,
  primary key (tenant_id, user_id)
);
select platform.enable_tenant_rls('experience.behavior_consent');
grant select, insert, update, delete on experience.behavior_consent to tb_app;

create table experience.behavior_signal (
  tenant_id uuid not null,
  user_id uuid not null,
  kind text not null check (kind in ('page')),
  key text not null check (length(key) between 1 and 300),
  day date not null,
  count int not null default 1 check (count >= 1),
  primary key (tenant_id, user_id, kind, key, day)
);
create index behavior_signal_day on experience.behavior_signal (tenant_id, day);
select platform.enable_tenant_rls('experience.behavior_signal');
grant select, insert, update, delete on experience.behavior_signal to tb_app;
