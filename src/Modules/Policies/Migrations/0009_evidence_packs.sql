-- Spec 025 US8 (R2, FR-016): evidence packs. A compliance officer asks for a pack of policies over a date range; it is
-- built in the background as PDF and CSV into object storage under the tenant's key, and may be shared read-only with
-- a space they own (an auditor guest space, spec 026) until an expiry date.
create table policies.evidence_pack (
  tenant_id uuid not null,
  id uuid not null,
  policy_ids uuid[] not null check (cardinality(policy_ids) between 1 and 20),
  from_on date not null,
  to_on date not null,
  locale text not null,
  status text not null check (status in ('queued', 'ready', 'failed')),
  requested_by uuid not null,
  requested_at timestamptz not null,
  completed_at timestamptz null,
  pdf_key text null,
  csv_key text null,
  shared_space_id uuid null,
  shared_until timestamptz null,
  primary key (tenant_id, id),
  check (from_on <= to_on),
  check ((shared_space_id is null) = (shared_until is null))
);
create index evidence_pack_requester on policies.evidence_pack (tenant_id, requested_by, requested_at desc);
create index evidence_pack_space on policies.evidence_pack (tenant_id, shared_space_id) where shared_space_id is not null;
select platform.enable_tenant_rls('policies.evidence_pack');
grant select, insert, update on policies.evidence_pack to tb_app;
