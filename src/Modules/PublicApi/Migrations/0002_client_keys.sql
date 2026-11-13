-- Spec 035 FR-005 (T006): public keys for private key JWT, one-time use of client assertions, and the expiry alert.
-- A public key credential keeps the public JWK and its thumbprint; a secret keeps only its salted hash.
alter table publicapi.app_credential add column public_jwk text null;
alter table publicapi.app_credential add column expiry_alerted_at timestamptz null;
alter table publicapi.app_credential add constraint app_credential_material check (
  (kind = 'secret' and secret_hash is not null and public_jwk is null) or (kind = 'public_key' and public_jwk is not null and secret_hash is null));
create index app_credential_expiring on publicapi.app_credential (tenant_id, expires_at) where revoked_at is null and expiry_alerted_at is null;

-- The JWT IDs of client assertions already used, kept until the assertion would have expired anyway (RFC 7523 section 3).
create table publicapi.client_assertion (
  tenant_id uuid not null,
  app_id uuid not null,
  jti text not null,
  expires_at timestamptz not null,
  primary key (tenant_id, app_id, jti),
  foreign key (tenant_id, app_id) references publicapi.app (tenant_id, id)
);
select platform.enable_tenant_rls('publicapi.client_assertion');
grant select, insert, delete on publicapi.client_assertion to tb_app;
