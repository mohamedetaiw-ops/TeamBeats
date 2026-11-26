-- Spec 009 R3 US11, FR-026, FR-027: a campaign step may follow an earlier step after a number of days, going only to
-- the people that step reached who have not acted on its item; a campaign may have a goal (an item of its steps and
-- the share of the audience that acted on it) that completes it and skips its pending steps when reached.
alter table content.campaign_step
  add column follows_step_id uuid null,
  add column delay_days int null check (delay_days between 1 and 60),
  add constraint campaign_step_follows check ((follows_step_id is null) = (delay_days is null));

alter table content.campaign
  add column goal_item_id uuid null,
  add column goal_percent int null check (goal_percent between 1 and 100),
  add column goal_reached_at timestamptz null,
  add constraint campaign_goal check ((goal_item_id is null) = (goal_percent is null));

create index campaign_goal_open on content.campaign (tenant_id) where status = 'scheduled' and goal_item_id is not null;
