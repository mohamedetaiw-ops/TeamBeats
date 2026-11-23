-- Spec 025 section 14 (R2): paper attestation import. A compliance officer attaches the scanned forms and imports a
-- CSV of who signed which version on which day; each row becomes an attestation with source "paper", channel "paper",
-- the importer and the import recorded. Online attestations stay the only ones a person makes for themselves.
alter table policies.attestation drop constraint attestation_channel_check;
alter table policies.attestation add constraint attestation_channel_check check (channel in ('web', 'mobile', 'teams', 'email', 'paper'));
alter table policies.attestation add column imported_by uuid null;
alter table policies.attestation add column import_id uuid null;
alter table policies.attestation add constraint attestation_paper_import check ((source = 'paper') = (import_id is not null and imported_by is not null));

create table policies.paper_import (
  tenant_id uuid not null,
  id uuid not null,
  policy_id uuid not null,
  imported_by uuid not null,
  created_at timestamptz not null,
  scan_key text not null,
  scan_name text not null,
  scan_type text not null,
  scan_size bigint not null,
  scan_sha256 text not null,
  status text not null check (status in ('draft', 'completed')),
  completed_at timestamptz null,
  imported int not null default 0,
  rejected jsonb not null default '[]',
  primary key (tenant_id, id),
  foreign key (tenant_id, policy_id) references policies.policy (tenant_id, id)
);
create index paper_import_policy on policies.paper_import (tenant_id, policy_id, created_at desc);
select platform.enable_tenant_rls('policies.paper_import');
grant select, insert, update on policies.paper_import to tb_app;
