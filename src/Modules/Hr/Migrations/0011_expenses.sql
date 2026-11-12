-- Spec 022 slice 6 (T023): expense claims (US8, FR-023, FR-025). Finance sets the policy and the exchange rates; a
-- claim's lines keep the amounts as converted on submission, so a later rate or limit change never changes them.
create table hr.expense_policy (
  tenant_id uuid not null primary key,
  -- {"currency": "AED", "windowDays": 60, "categories": [...], "tiers": [...], "costCentres": [...]}
  policy jsonb not null,
  version int not null default 1,
  updated_by uuid null,
  updated_at timestamptz null
);
select platform.enable_tenant_rls('hr.expense_policy');
grant select, insert, update on hr.expense_policy to tb_app;

-- Units of the policy currency for one unit of the currency, from the receipt date on (the manual rate source).
create table hr.expense_rate (
  tenant_id uuid not null,
  currency text not null check (currency ~ '^[A-Z]{3}$'),
  rate_date date not null,
  rate numeric(18, 8) not null check (rate > 0),
  updated_by uuid not null,
  updated_at timestamptz not null,
  primary key (tenant_id, currency, rate_date)
);
select platform.enable_tenant_rls('hr.expense_rate');
grant select, insert, update, delete on hr.expense_rate to tb_app;

create table hr.expense_receipt (
  tenant_id uuid not null,
  id uuid not null,
  uploaded_by uuid not null,
  file_name text not null,
  size_bytes bigint not null,
  content_type text not null,
  -- SHA-256 of the file, for duplicate detection (section 13: receipt hash).
  sha256 text not null,
  scan_state text not null check (scan_state in ('clean', 'failed')),
  claim_id uuid null,
  created_at timestamptz not null,
  primary key (tenant_id, id)
);
create index expense_receipt_hash on hr.expense_receipt (tenant_id, sha256);
select platform.enable_tenant_rls('hr.expense_receipt');
grant select, insert, update on hr.expense_receipt to tb_app;

create table hr.expense_counter (
  tenant_id uuid not null primary key,
  next_number int not null
);
select platform.enable_tenant_rls('hr.expense_counter');
grant select, insert, update on hr.expense_counter to tb_app;

create table hr.expense_claim (
  tenant_id uuid not null,
  id uuid not null,
  reference text not null,
  user_id uuid not null,
  org_unit_id uuid null,
  title text not null check (char_length(title) between 1 and 200),
  cost_centre text null,
  project text null,
  currency text not null,
  total numeric(14, 2) not null check (total > 0),
  duplicate boolean not null default false,
  over_limit boolean not null default false,
  status text not null check (status in ('pending', 'approved', 'handed_off', 'paid', 'handoff_failed', 'rejected', 'withdrawn')),
  -- The finance hand-off (spec 017): unavailable when the tenant has no finance connection.
  finance_state text null,
  external_reference text null,
  payment_status text null,
  instance_id uuid null,
  policy_version int not null,
  decided_by uuid null,
  decided_at timestamptz null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, reference)
);
create index expense_claim_user on hr.expense_claim (tenant_id, user_id, created_at desc);
select platform.enable_tenant_rls('hr.expense_claim');
grant select, insert, update on hr.expense_claim to tb_app;

create table hr.expense_line (
  tenant_id uuid not null,
  claim_id uuid not null,
  n int not null,
  category text not null,
  kind text not null check (kind in ('amount', 'per_diem', 'mileage')),
  expense_date date not null,
  merchant text null,
  description text null,
  amount numeric(14, 2) not null check (amount > 0),
  currency text not null,
  tax numeric(14, 2) null,
  days int null,
  km numeric(8, 1) null,
  rate numeric(18, 8) not null,
  base_amount numeric(14, 2) not null check (base_amount > 0),
  limit_amount numeric(14, 2) null,
  justification text null,
  receipt_id uuid null,
  duplicate boolean not null default false,
  primary key (tenant_id, claim_id, n)
);
create index expense_line_match on hr.expense_line (tenant_id, expense_date, amount, currency);
select platform.enable_tenant_rls('hr.expense_line');
grant select, insert, update on hr.expense_line to tb_app;
