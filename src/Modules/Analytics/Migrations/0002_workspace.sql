-- Spec 021 R2 T010: the membership type at event time, for the workspace filter of FR-006 (employee, contractor, guest
-- and the other tenant membership types). Older events have none and match only an unfiltered query.
alter table analytics.event add column membership_type text null check (membership_type is null or membership_type ~ '^[a-z_]{1,40}$');
create index event_name_unit_time on analytics.event (tenant_id, name, org_unit_id, occurred_at);
