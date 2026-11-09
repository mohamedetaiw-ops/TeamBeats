-- Spec 025 FR-008: a person may attest to the same version again in a later campaign (a yearly re-attestation of an
-- unchanged policy), so the record is unique per campaign and person, not per version.
alter table policies.attestation drop constraint attestation_tenant_id_user_id_policy_id_version_key;
create unique index attestation_campaign_user on policies.attestation (tenant_id, campaign_id, user_id) where campaign_id is not null;
create index attestation_user on policies.attestation (tenant_id, user_id, policy_id, attested_at desc);
