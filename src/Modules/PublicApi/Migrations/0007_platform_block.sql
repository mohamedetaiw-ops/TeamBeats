-- Spec 035 FR-028, US6 scenario 4 (slice 5a): the platform operator's block of a client ID, for a vulnerability or
-- abuse. A blocked client ID stops working in every tenant at once, whatever its kind; the tenant's registration shows
-- it as blocked. Platform data, no tenant column, no tenant data.
create table publicapi.platform_block (
  client_id text primary key check (length(client_id) between 1 and 200),
  reason text not null check (length(reason) between 1 and 500),
  blocked_by uuid not null,
  blocked_at timestamptz not null
);
grant select, insert, delete on publicapi.platform_block to tb_app;
