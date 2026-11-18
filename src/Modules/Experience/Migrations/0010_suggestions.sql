-- Spec 008 R2 T025 (FR-035): safe suggestions. A visit counts toward "pin a page you open often"; the person's answer
-- to each suggestion is kept so it is never offered again, and an accepted one keeps what undo needs. Nothing here
-- changes a preference: only an accepted suggestion does, through the same paths the person would use themselves.
alter table experience.recent_visit add column visit_count int not null default 1 check (visit_count >= 1);

create table experience.suggestion_decision (
  tenant_id uuid not null,
  user_id uuid not null,
  key text not null check (length(key) between 1 and 400),
  state text not null check (state in ('accepted', 'declined', 'undone')),
  undo jsonb null,
  decided_at timestamptz not null,
  primary key (tenant_id, user_id, key)
);
select platform.enable_tenant_rls('experience.suggestion_decision');
grant select, insert, update, delete on experience.suggestion_decision to tb_app;
