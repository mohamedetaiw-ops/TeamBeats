-- Spec 022 slice 5 (T021): HR letters and certificates. Letter types carry a bilingual template, versioned; issued
-- letters keep their own PDF and what the verification page may show, so a template change never changes them.

create table hr.letter_settings (
  tenant_id uuid not null primary key,
  -- The public verification page (US7 scenario 2, open question default: on, the tenant can turn it off).
  verification_enabled boolean not null default true,
  version int not null default 1,
  updated_by uuid null,
  updated_at timestamptz null
);
select platform.enable_tenant_rls('hr.letter_settings');
grant select, insert, update on hr.letter_settings to tb_app;

create table hr.letter_type (
  tenant_id uuid not null,
  id uuid not null,
  key text not null check (key ~ '^[a-z][a-z0-9_]{1,39}$'),
  names jsonb not null,
  -- Plain text per language with {placeholders}; rendered as text, never as markup.
  templates jsonb not null,
  -- Values HR types in before issuing, such as a salary: [{"key": "salary", "names": {"en": "...", "ar": "..."}}].
  manual_fields jsonb not null default '[]',
  needs_approval boolean not null default true,
  org_unit_id uuid null,
  active boolean not null default true,
  version int not null default 1,
  updated_by uuid null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, key),
  check (manual_fields = '[]'::jsonb or needs_approval)
);
select platform.enable_tenant_rls('hr.letter_type');
grant select, insert, update on hr.letter_type to tb_app;

create table hr.letter_counter (
  tenant_id uuid not null primary key,
  next_number int not null
);
select platform.enable_tenant_rls('hr.letter_counter');
grant select, insert, update on hr.letter_counter to tb_app;

create table hr.letter (
  tenant_id uuid not null,
  id uuid not null,
  reference text not null,
  user_id uuid not null,
  org_unit_id uuid null,
  letter_type_id uuid not null,
  type_version int not null,
  -- What the verification page shows, kept as issued.
  type_names jsonb not null,
  language text not null check (language in ('en', 'ar')),
  addressee text null check (addressee is null or char_length(addressee) between 1 and 200),
  -- HR's values for the type's manual fields; cleared once the letter is issued, since the PDF holds them.
  manual_values jsonb not null default '{}',
  status text not null check (status in ('pending', 'approved', 'issued', 'rejected', 'withdrawn')),
  instance_id uuid null,
  decided_by uuid null,
  decided_at timestamptz null,
  -- Only the SHA-256 of the verification code is kept; the code itself is printed on the PDF.
  code_hash bytea null,
  company_name text null,
  object_key text null,
  issued_at timestamptz null,
  issue_attempts int not null default 0,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, reference),
  foreign key (tenant_id, letter_type_id) references hr.letter_type (tenant_id, id),
  check ((status = 'issued') = (issued_at is not null and object_key is not null and code_hash is not null))
);
create unique index letter_code on hr.letter (tenant_id, code_hash) where code_hash is not null;
create index letter_user on hr.letter (tenant_id, user_id, created_at desc);
create index letter_to_issue on hr.letter (tenant_id) where status = 'approved';
select platform.enable_tenant_rls('hr.letter');
grant select, insert, update on hr.letter to tb_app;
