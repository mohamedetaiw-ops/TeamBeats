-- Spec 026 (R1): the tenant security policy (network rules, MFA assurance, session rules), the MFA claim mapping per
-- identity provider, and security alerts. Every table is tenant-owned with forced row-level security.
create schema security;
grant usage on schema security to tb_app;

create table security.policy (
  tenant_id uuid not null primary key,
  ip_allow cidr[] not null default '{}',
  ip_block cidr[] not null default '{}',
  admin_ip_allow cidr[] not null default '{}',
  network_zones jsonb not null default '{}'::jsonb,
  require_mfa boolean not null default false,
  step_up_minutes int not null default 10 check (step_up_minutes between 1 and 60),
  idle_minutes int not null default 30 check (idle_minutes between 5 and 1440),
  absolute_hours int not null default 12 check (absolute_hours between 1 and 72),
  max_sessions int null check (max_sessions between 1 and 20),
  remember_device_days int not null default 0 check (remember_device_days between 0 and 90),
  alert_settings jsonb not null default '{}'::jsonb,
  version int not null default 1,
  updated_at timestamptz not null default now(),
  updated_by uuid null
);
select platform.enable_tenant_rls('security.policy');
grant select, insert, update on security.policy to tb_app;

-- Which amr or acr values count as MFA for an identity provider; seen_at is when a sign-in last showed one.
create table security.mfa_claim_mapping (
  tenant_id uuid not null,
  provider text not null,
  amr text[] not null default '{mfa}',
  acr text[] not null default '{}',
  seen_at timestamptz null,
  primary key (tenant_id, provider)
);
select platform.enable_tenant_rls('security.mfa_claim_mapping');
grant select, insert, update on security.mfa_claim_mapping to tb_app;

-- Facts are counts and references only, never content.
create table security.alert (
  tenant_id uuid not null,
  id uuid not null,
  kind text not null,
  subject_user_id uuid null,
  facts jsonb not null default '{}'::jsonb,
  status text not null default 'open' check (status in ('open', 'handled', 'dismissed')),
  handled_by uuid null,
  handled_at timestamptz null,
  note text null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id)
);
create index alert_open on security.alert (tenant_id, created_at desc) where status = 'open';
select platform.enable_tenant_rls('security.alert');
grant select, insert, update on security.alert to tb_app;
