-- Spec 012 FR-013 and US8 scenario 2 (R2): the pseudonymous search query log that feeds the search dashboard of spec
-- 021. Each first page of results is one event with the person's analytics key (never their user ID), the language,
-- the number of results and the first click on a result. The query is kept only as its normalized words, and not at
-- all when the tenant turned query text off; turning it off also clears the words already kept.
create table search.query_event (
  tenant_id uuid not null,
  id uuid not null,
  at timestamptz not null,
  person text not null,
  locale text not null,
  query_key text null check (length(query_key) <= 200),
  results int not null,
  clicked_at timestamptz null,
  click_position int null,
  primary key (tenant_id, id)
);
create index query_event_at on search.query_event (tenant_id, at);
select platform.enable_tenant_rls('search.query_event');
grant select, insert, update, delete on search.query_event to tb_app;

alter table search.setting alter column ai_summary set default false;
alter table search.setting add column query_text boolean not null default true;
