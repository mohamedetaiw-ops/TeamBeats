-- Spec 020 R2 (US8 scenarios 2 to 4, FR-014): daily briefings. A person subscribes to an agent that offers one, at a
-- time of day on chosen weekdays in their time zone. A worker runs each due briefing as that person, read only, through
-- the normal runtime, and delivers the answer as a notification. Opting out deletes the row.
create table agents.agent_briefing (
  tenant_id uuid not null,
  user_id uuid not null,
  agent_id uuid not null,
  time text not null check (time ~ '^([01][0-9]|2[0-3]):[0-5][0-9]$'),
  days int[] not null check (cardinality(days) between 1 and 7 and days <@ array[1, 2, 3, 4, 5, 6, 7]),
  time_zone text not null check (length(time_zone) between 1 and 64),
  created_at timestamptz not null,
  next_run_at timestamptz not null,
  last_run_at timestamptz null,
  last_run_id uuid null,
  primary key (tenant_id, user_id, agent_id)
);
create index agent_briefing_due on agents.agent_briefing (tenant_id, next_run_at);
select platform.enable_tenant_rls('agents.agent_briefing');
grant select, insert, update, delete on agents.agent_briefing to tb_app;

alter table agents.run drop constraint run_mode_check;
alter table agents.run add constraint run_mode_check check (mode in ('run', 'test', 'eval', 'briefing'));
