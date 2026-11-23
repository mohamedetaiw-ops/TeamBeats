-- Spec 025 FR-003 (second half): a policy version kept as a signed PDF in a spec 011 controlled document. The version
-- keeps which document and which released version it was made from; its clauses hold the text extracted from it.
alter table policies.policy_version add column document jsonb null;
