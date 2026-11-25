-- Spec 011 R3: records declaration and disposition certificates.

-- A label that declares records makes the documents it labels records once they are published and checked in.
alter table documents.retention_label add column declares_record boolean not null default false;

-- A record refuses every change but disposition. record_source says whether a records manager declared it or a label did.
alter table documents.document add column record_at timestamptz null;
alter table documents.document add column record_by uuid null;
alter table documents.document add column record_source text null check (record_source in ('manual', 'label'));
alter table documents.document add constraint document_record check ((record_at is null) = (record_source is null));
create index document_record on documents.document (tenant_id, record_at desc) where record_at is not null and deleted_at is null;

-- A disposition certificate covers a fixed list of disposition records in a date range, with a digest over them.
create table documents.disposition_certificate (
  tenant_id uuid not null,
  id uuid not null,
  code text not null check (code ~ '^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$'),
  from_at timestamptz not null,
  to_at timestamptz not null,
  records int not null check (records > 0),
  digest text not null check (digest ~ '^[0-9a-f]{64}$'),
  issued_by uuid not null,
  issued_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, code),
  check (from_at < to_at)
);
create index disposition_certificate_at on documents.disposition_certificate (tenant_id, issued_at desc);
select platform.enable_tenant_rls('documents.disposition_certificate');
grant select, insert on documents.disposition_certificate to tb_app;

create table documents.disposition_certificate_item (
  tenant_id uuid not null,
  certificate_id uuid not null,
  record_id uuid not null,
  position int not null,
  primary key (tenant_id, certificate_id, position),
  foreign key (tenant_id, certificate_id) references documents.disposition_certificate (tenant_id, id),
  foreign key (tenant_id, record_id) references documents.disposition_record (tenant_id, id)
);
create index disposition_certificate_item_record on documents.disposition_certificate_item (tenant_id, record_id);
select platform.enable_tenant_rls('documents.disposition_certificate_item');
grant select, insert on documents.disposition_certificate_item to tb_app;
