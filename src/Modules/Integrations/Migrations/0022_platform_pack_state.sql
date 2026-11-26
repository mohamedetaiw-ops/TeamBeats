-- Spec 017 R3 (connector marketplace, section 3): the platform operator turns a connector pack off for all tenants,
-- with a reason. A pack turned off cannot be added and its connections make no calls until it is turned on again.
-- Platform data, no tenant column, no tenant data.
create table integrations.platform_pack_state (
  kind text primary key check (length(kind) between 1 and 50),
  reason text not null check (length(reason) between 1 and 500),
  turned_off_by uuid not null,
  turned_off_at timestamptz not null
);
grant select, insert, update, delete on integrations.platform_pack_state to tb_app;
