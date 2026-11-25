-- Spec 035 R3 (T021): sender-constrained tokens with DPoP (RFC 9449). An app can require that every token it gets is
-- bound to a key it holds; a proof is good once, so its JWT ID is kept until the proof would be too old anyway.
alter table publicapi.app add column require_dpop boolean not null default false;

create table publicapi.dpop_proof (
  tenant_id uuid not null,
  jti_hash text not null,
  expires_at timestamptz not null,
  primary key (tenant_id, jti_hash)
);
create index dpop_proof_expires on publicapi.dpop_proof (tenant_id, expires_at);
select platform.enable_tenant_rls('publicapi.dpop_proof');
grant select, insert, delete on publicapi.dpop_proof to tb_app;
