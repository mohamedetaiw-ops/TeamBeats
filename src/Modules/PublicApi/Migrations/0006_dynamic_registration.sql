-- Spec 035 FR-023 (slice 4e): a tenant MAY turn on dynamic client registration (RFC 7591) for MCP clients. Off by
-- default. Each client that registers itself is held as a pending public MCP client app, with no creator, until an API
-- administrator approves it; until then the authorization server does not know it.
alter table publicapi.setting add column dynamic_registration boolean not null default false;

alter table publicapi.app drop constraint app_status_check;
alter table publicapi.app add constraint app_status_check check (status in ('active', 'disabled', 'pending'));
alter table publicapi.app alter column created_by drop not null;
alter table publicapi.app add column self_registered boolean not null default false;
alter table publicapi.app add constraint app_self_registered_creator check (self_registered or created_by is not null);
