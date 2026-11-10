-- Spec 017 (R2) T014: on-premises sync agents. A one-time enrolment code per connection, the agents enrolled with it
-- (each bound to one tenant and one connection by its public key), the jobs agents pull with a lease so one agent at a
-- time works for a connection, and directory records an agent returns for spec 003. Tenant-owned, forced RLS.
create table integrations.agent_enrolment (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  code_hash text not null,
  expires_at timestamptz not null,
  used_at timestamptz null,
  created_by uuid null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create unique index agent_enrolment_code on integrations.agent_enrolment (tenant_id, code_hash);
select platform.enable_tenant_rls('integrations.agent_enrolment');
grant select, insert, update, delete on integrations.agent_enrolment to tb_app;

create table integrations.sync_agent (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  name text not null check (length(name) between 1 and 100),
  public_key text not null,
  thumbprint text not null check (thumbprint ~ '^[0-9a-f]{64}$'),
  enrolled_at timestamptz not null,
  last_contact_at timestamptz null,
  -- The newest signed request timestamp accepted, so an older signed request cannot be replayed.
  last_request_ts bigint not null default 0,
  version text null check (version is null or length(version) <= 40),
  revoked_at timestamptz null,
  revoked_by uuid null,
  primary key (tenant_id, id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create unique index sync_agent_thumbprint on integrations.sync_agent (tenant_id, thumbprint);
create index sync_agent_connection on integrations.sync_agent (tenant_id, connection_id);
select platform.enable_tenant_rls('integrations.sync_agent');
grant select, insert, update, delete on integrations.sync_agent to tb_app;

create table integrations.agent_job (
  tenant_id uuid not null,
  id uuid not null,
  connection_id uuid not null,
  kind text not null check (kind in ('directory_sync')),
  status text not null check (status in ('queued', 'taken', 'succeeded', 'failed')),
  payload jsonb not null default '{}',
  taken_by uuid null,
  lease_until timestamptz null,
  attempts int not null default 0,
  error_class text null,
  user_count int null,
  group_count int null,
  created_by uuid null,
  created_at timestamptz not null default now(),
  finished_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, connection_id) references integrations.connection (tenant_id, id) on delete cascade
);
create index agent_job_queue on integrations.agent_job (tenant_id, connection_id, created_at) where status in ('queued', 'taken');
select platform.enable_tenant_rls('integrations.agent_job');
grant select, insert, update, delete on integrations.agent_job to tb_app;

-- Canonical directory records an agent returned for a job (DirectoryUserRecord and DirectoryGroupRecord as JSON).
create table integrations.agent_directory_record (
  tenant_id uuid not null,
  job_id uuid not null,
  kind text not null check (kind in ('user', 'group')),
  external_id text not null check (length(external_id) between 1 and 200),
  record jsonb not null,
  primary key (tenant_id, job_id, kind, external_id),
  foreign key (tenant_id, job_id) references integrations.agent_job (tenant_id, id) on delete cascade
);
select platform.enable_tenant_rls('integrations.agent_directory_record');
grant select, insert, update, delete on integrations.agent_directory_record to tb_app;
