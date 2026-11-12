-- Spec 018 US5 and spec 031 FR-002: the cache of the tenant's Microsoft 365 room mailboxes. The 018 Rooms feature fills
-- it from Microsoft Graph places at each refresh; workplace booking (031) reads it so that a room is never both a
-- Microsoft 365 room mailbox and a TeamBeats room. Addresses are stored in lower case.
create table integrations.room_mailbox (
  tenant_id uuid not null,
  address text not null check (address = lower(address)),
  display_name text not null,
  refreshed_at timestamptz not null,
  primary key (tenant_id, address)
);
select platform.enable_tenant_rls('integrations.room_mailbox');
grant select, insert, update, delete on integrations.room_mailbox to tb_app;
