-- Spec 023 (R2) slice 6: recognition and points (US4, FR-011 to FR-013). A shout-out thanks people or a team, may name
-- a company value and a custom badge, and shows on the recognition wall and, where the person allows it, on their
-- profile. Points are configured per action with a daily cap, can be switched off, and carry no monetary value.
-- Every table is tenant-owned with forced row-level security.

-- Company values that a shout-out can name, configured by engagement administrators.
create table engagement.recognition_value (
  tenant_id uuid not null,
  id uuid not null,
  key text not null check (key ~ '^[a-z0-9][a-z0-9_-]{0,39}$'),
  names jsonb not null,
  active boolean not null default true,
  position int not null,
  primary key (tenant_id, id),
  unique (tenant_id, key)
);
select platform.enable_tenant_rls('engagement.recognition_value');
grant select, insert, update on engagement.recognition_value to tb_app;

-- Custom badges. giver_unit_id limits who may give it to people in that unit or below; null means everyone.
create table engagement.recognition_badge (
  tenant_id uuid not null,
  id uuid not null,
  key text not null check (key ~ '^[a-z0-9][a-z0-9_-]{0,39}$'),
  names jsonb not null,
  icon text not null,
  giver_unit_id uuid null,
  active boolean not null default true,
  position int not null,
  primary key (tenant_id, id),
  unique (tenant_id, key)
);
select platform.enable_tenant_rls('engagement.recognition_badge');
grant select, insert, update on engagement.recognition_badge to tb_app;

-- A shout-out to people, or to a team (an org unit) with no people listed.
create table engagement.recognition (
  tenant_id uuid not null,
  id uuid not null,
  giver_id uuid not null,
  message text not null check (length(message) between 1 and 1000),
  value_id uuid null,
  badge_id uuid null,
  team_unit_id uuid null,
  status text not null default 'visible' check (status in ('visible', 'removed')),
  counted boolean not null default false,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  foreign key (tenant_id, value_id) references engagement.recognition_value (tenant_id, id),
  foreign key (tenant_id, badge_id) references engagement.recognition_badge (tenant_id, id)
);
create index recognition_wall on engagement.recognition (tenant_id, status, created_at desc);
create index recognition_giver on engagement.recognition (tenant_id, giver_id, created_at desc);
select platform.enable_tenant_rls('engagement.recognition');
grant select, insert, update on engagement.recognition to tb_app;

create table engagement.recognition_recipient (
  tenant_id uuid not null,
  recognition_id uuid not null,
  user_id uuid not null,
  primary key (tenant_id, recognition_id, user_id),
  foreign key (tenant_id, recognition_id) references engagement.recognition (tenant_id, id)
);
create index recognition_recipient_user on engagement.recognition_recipient (tenant_id, user_id);
select platform.enable_tenant_rls('engagement.recognition_recipient');
grant select, insert on engagement.recognition_recipient to tb_app;

-- A person's choices: whether shout-outs they receive show on their profile, and whether they appear on leaderboards.
create table engagement.recognition_preference (
  tenant_id uuid not null,
  user_id uuid not null,
  show_on_profile boolean not null default true,
  leaderboard_opt_out boolean not null default false,
  updated_at timestamptz not null,
  primary key (tenant_id, user_id)
);
select platform.enable_tenant_rls('engagement.recognition_preference');
grant select, insert, update on engagement.recognition_preference to tb_app;

-- The tenant's points configuration (versioned, changed with If-Match). No row means the defaults.
create table engagement.points_setting (
  tenant_id uuid not null primary key,
  enabled boolean not null,
  leaderboard boolean not null,
  recognition_received int not null check (recognition_received between 0 and 1000),
  post int not null check (post between 0 and 1000),
  poll_vote int not null check (poll_vote between 0 and 1000),
  daily_cap int not null check (daily_cap between 1 and 10000),
  giver_daily_limit int not null check (giver_daily_limit between 1 and 100),
  version int not null default 1,
  updated_by uuid not null,
  updated_at timestamptz not null
);
select platform.enable_tenant_rls('engagement.points_setting');
grant select, insert, update on engagement.points_setting to tb_app;

-- Points earned. One entry per action, source and person; earned_on is the tenant's day, for the daily cap and periods.
create table engagement.point_entry (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  action text not null check (action in ('recognition_received', 'post', 'poll_vote')),
  source_id uuid not null,
  points int not null check (points > 0),
  earned_on date not null,
  created_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, action, source_id, user_id)
);
create index point_entry_user on engagement.point_entry (tenant_id, user_id, earned_on);
create index point_entry_period on engagement.point_entry (tenant_id, earned_on);
select platform.enable_tenant_rls('engagement.point_entry');
grant select, insert, delete on engagement.point_entry to tb_app;
