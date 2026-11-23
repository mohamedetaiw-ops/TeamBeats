-- Spec 025 edge case (R2): a person on long leave during a campaign. Compliance pauses their deadline with a reason
-- until the day they are back; the deadline moves by the time paused and nothing is sent or escalated meanwhile.
alter table policies.campaign_member add column paused_until timestamptz null;
alter table policies.campaign_member add column pause_reason text null check (pause_reason is null or length(pause_reason) between 1 and 500);
alter table policies.campaign_member add column paused_by uuid null;
alter table policies.campaign_member add column paused_at timestamptz null;
alter table policies.campaign_member add constraint campaign_member_pause check ((paused_until is null) = (pause_reason is null) and (paused_until is null) = (paused_by is null));
