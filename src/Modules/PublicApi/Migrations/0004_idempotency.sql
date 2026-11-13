-- Spec 035 FR-015 (T010): Idempotency-Key on public create and action operations. The first answer to a key is kept
-- 24 hours and replayed; the same key with another request is refused. A key is held while its first call runs, and a
-- hold older than 5 minutes is treated as abandoned. Answers larger than 64 KB, and server errors, are not kept.
create table publicapi.idempotency (
  tenant_id uuid not null,
  app_id uuid not null,
  key text not null check (length(key) between 1 and 255),
  request_hash text not null,
  status int null,
  content_type text null,
  body text null,
  created_at timestamptz not null,
  primary key (tenant_id, app_id, key),
  foreign key (tenant_id, app_id) references publicapi.app (tenant_id, id)
);
create index idempotency_created on publicapi.idempotency (tenant_id, created_at);
select platform.enable_tenant_rls('publicapi.idempotency');
grant select, insert, update, delete on publicapi.idempotency to tb_app;
