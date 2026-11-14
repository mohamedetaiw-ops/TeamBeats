-- Spec 012 US7 (R2, FR-019): searches a person saved, optionally with a daily alert for new items that match. A saved
-- search belongs to its person only; it is personal data of the tenant and goes with the person's account.
-- checked_at is when new items were last looked for: an alert covers items first indexed after it, at most once a day.
create table search.saved_search (
  tenant_id uuid not null,
  id uuid not null,
  user_id uuid not null,
  query text not null check (length(query) between 1 and 200),
  alerts boolean not null,
  created_at timestamptz not null,
  checked_at timestamptz not null,
  alerted_at timestamptz null,
  primary key (tenant_id, id)
);
create unique index saved_search_unique on search.saved_search (tenant_id, user_id, query);
create index saved_search_due on search.saved_search (tenant_id, checked_at) where alerts;
select platform.enable_tenant_rls('search.saved_search');
grant select, insert, update, delete on search.saved_search to tb_app;
