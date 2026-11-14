-- Spec 011 R2 legal hold, first part: holds on individual resources of any module, asked before every deletion.
create table documents.legal_hold (
  tenant_id uuid not null,
  resource_type text not null check (length(resource_type) between 1 and 100),
  resource_id uuid not null,
  reason text not null check (length(reason) between 1 and 500),
  placed_by uuid null,
  placed_at timestamptz not null,
  primary key (tenant_id, resource_type, resource_id)
);
select platform.enable_tenant_rls('documents.legal_hold');
grant select, insert, delete on documents.legal_hold to tb_app;
