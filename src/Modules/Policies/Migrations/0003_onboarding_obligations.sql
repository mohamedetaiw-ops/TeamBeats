-- Spec 025 FR-017 with spec 022 FR-015: a policy can be flagged "onboarding required", and a new joiner's onboarding
-- journey asks them to attest to every such policy that applies to them, by the journey's deadline. An obligation is
-- the person's own attestation task outside any campaign; source_key names the journey step that asked for it.
alter table policies.policy add column onboarding_required boolean not null default false;

create table policies.obligation (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  policy_id uuid not null,
  version int not null,
  source_key text not null check (length(source_key) between 1 and 200),
  due_at timestamptz not null,
  status text not null check (status in ('open', 'attested', 'cancelled')),
  created_at timestamptz not null,
  attested_at timestamptz null,
  primary key (tenant_id, id),
  unique (tenant_id, source_key, policy_id),
  foreign key (tenant_id, policy_id) references policies.policy (tenant_id, id)
);
create index obligation_user on policies.obligation (tenant_id, user_id, status);
select platform.enable_tenant_rls('policies.obligation');
grant select, insert, update on policies.obligation to tb_app;
