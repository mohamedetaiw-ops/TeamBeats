-- Spec 022 T019 part 2: pre-boarding (US4 scenario 3, FR-016) behind a tenant setting, off until HR turns it on.

create table hr.journey_settings (
  tenant_id uuid not null primary key,
  preboarding_enabled boolean not null default false,
  version int not null default 1,
  updated_by uuid null,
  updated_at timestamptz null
);
select platform.enable_tenant_rls('hr.journey_settings');
grant select, insert, update on hr.journey_settings to tb_app;

-- One invitation per hire HR knows before the account exists. The personal email is not kept here: it is read from
-- HR when the invitation is sent. Only a hash of the link's token is stored.
create table hr.preboarding (
  tenant_id uuid not null,
  id uuid not null,
  record_id text not null check (length(record_id) between 1 and 100),
  template_id uuid not null,
  start_date date not null,
  send_on date not null,
  status text not null check (status in ('scheduled', 'sent', 'cancelled', 'failed')),
  attempts int not null default 0,
  not_before timestamptz null,
  token_hash bytea null,
  expires_at timestamptz null,
  sent_at timestamptz null,
  opened_at timestamptz null,
  ended_reason text null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id),
  unique (tenant_id, record_id),
  foreign key (tenant_id, template_id) references hr.journey_template (tenant_id, id),
  check (status <> 'sent' or (token_hash is not null and expires_at is not null and sent_at is not null))
);
create unique index preboarding_token on hr.preboarding (tenant_id, token_hash) where token_hash is not null;
create index preboarding_due on hr.preboarding (tenant_id, send_on) where status = 'scheduled';
select platform.enable_tenant_rls('hr.preboarding');
grant select, insert, update on hr.preboarding to tb_app;

-- A pre-boarding step's text, by language, copied from the template like the step's names.
alter table hr.journey_step add column content jsonb null;
alter table hr.journey_step drop constraint if exists journey_step_type_check;
alter table hr.journey_step add constraint journey_step_type_check
  check (type in ('task', 'attestation', 'access_removal', 'exit_interview', 'preboarding'));
