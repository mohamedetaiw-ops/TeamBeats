-- Spec 025 US7 (R2, FR-014): exemptions. A person, or a compliance officer for someone else or for a unit and its
-- subtree, asks to be exempted from a policy with a reason and an end date. The request goes through the exemption
-- route (the policy owner, then the compliance officers) in the task inbox. Approved exemptions are the register; the
-- person and the owner hear 14 days before the end, and on the end date the exemption closes on its own. One request
-- or exemption at a time per policy and person or unit.
create table policies.exemption (
  tenant_id uuid not null,
  id uuid not null,
  policy_id uuid not null,
  version int null,
  scope text not null check (scope in ('person', 'unit')),
  subject_user_id uuid null,
  org_unit_id uuid null,
  reason text not null check (length(reason) between 1 and 2000),
  ends_on date not null,
  ends_at timestamptz not null,
  status text not null check (status in ('pending', 'approved', 'rejected', 'withdrawn', 'expired')),
  requested_by uuid not null,
  requested_at timestamptz not null,
  instance_id uuid null,
  approvals jsonb not null default '[]',
  decided_by uuid null,
  decided_at timestamptz null,
  decision_comment text null,
  reminded_at timestamptz null,
  closed_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, policy_id) references policies.policy (tenant_id, id),
  check ((scope = 'person') = (subject_user_id is not null) and (scope = 'unit') = (org_unit_id is not null))
);
create unique index exemption_live on policies.exemption (tenant_id, policy_id, coalesce(subject_user_id, org_unit_id)) where status in ('pending', 'approved');
create index exemption_ends on policies.exemption (tenant_id, ends_at) where status = 'approved';
select platform.enable_tenant_rls('policies.exemption');
grant select, insert, update on policies.exemption to tb_app;
