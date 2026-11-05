-- Spec 006: append-only, per-tenant, hash-chained audit (ADR-0008).
create schema audit;
grant usage on schema audit to tb_app;

create table audit.audit_event (
  tenant_id uuid not null,
  id uuid not null,
  occurred_at timestamptz not null,
  actor_id uuid null,
  actor_type text not null,
  action text not null,
  target_type text not null,
  target_id text null,
  target_org_unit_id uuid null,
  outcome text not null,
  correlation_id text null,
  source_ip text null,
  changes jsonb null,
  chain_pos bigint null,
  prev_hash text null,
  hash text null,
  sealed_at timestamptz null,
  primary key (tenant_id, id)
);
create index audit_event_search on audit.audit_event (tenant_id, occurred_at desc, id desc);
create index audit_event_action on audit.audit_event (tenant_id, action, occurred_at desc);
create unique index audit_event_chain on audit.audit_event (tenant_id, chain_pos) where chain_pos is not null;
create index audit_event_unsealed on audit.audit_event (tenant_id, occurred_at, id) where chain_pos is null;
select platform.enable_tenant_rls('audit.audit_event');
-- Append-only for the application role: no UPDATE, no DELETE (AUD-002).
grant select, insert on audit.audit_event to tb_app;

-- Platform stream (tenant creation, lifecycle, break-glass). RLS exception, see rls-exceptions.md.
create table audit.platform_audit_event (
  id uuid primary key,
  occurred_at timestamptz not null,
  actor_id uuid null,
  actor_type text not null,
  action text not null,
  target_type text not null,
  target_id text null,
  subject_tenant_id uuid null,
  outcome text not null,
  correlation_id text null,
  source_ip text null,
  changes jsonb null
);
create index platform_audit_event_time on audit.platform_audit_event (occurred_at desc);
grant select, insert on audit.platform_audit_event to tb_app;

create table audit.chain_state (
  tenant_id uuid primary key,
  last_chain_pos bigint not null,
  last_hash text not null
);
select platform.enable_tenant_rls('audit.chain_state');
grant select on audit.chain_state to tb_app;

create table audit.retention (
  tenant_id uuid primary key,
  days int not null check (days >= 365),
  updated_at timestamptz not null default now()
);
select platform.enable_tenant_rls('audit.retention');
grant select, insert, update on audit.retention to tb_app;

create table audit.export_job (
  tenant_id uuid not null,
  id uuid not null,
  requested_by uuid not null,
  format text not null check (format in ('csv', 'json')),
  filter jsonb not null,
  status text not null check (status in ('pending', 'completed', 'failed')),
  object_key text null,
  row_count bigint null,
  error text null,
  created_at timestamptz not null default now(),
  completed_at timestamptz null,
  expires_at timestamptz null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('audit.export_job');
grant select, insert, update on audit.export_job to tb_app;

-- Canonical text of a sealed row. Changing this function invalidates every chain, so it is versioned by migration.
create function audit.canonical(r audit.audit_event, pos bigint) returns text
language sql immutable
as $$
  select concat_ws('|',
    pos::text, r.tenant_id::text, r.id::text,
    to_char(r.occurred_at at time zone 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
    coalesce(r.actor_id::text, ''), r.actor_type, r.action, r.target_type,
    coalesce(r.target_id, ''), coalesce(r.target_org_unit_id::text, ''), r.outcome,
    coalesce(r.correlation_id, ''), coalesce(r.source_ip, ''), coalesce(r.changes::text, ''))
$$;

-- Seals committed, unsealed rows of the current tenant in (occurred_at, id) order (ADR-0008).
-- SECURITY DEFINER so tb_app can fill the chain columns exactly once without holding UPDATE on the table.
create function audit.seal_pending(max_rows int) returns int
language plpgsql security definer
set search_path = pg_catalog, audit, platform
as $$
declare
  t uuid := platform.current_tenant_id();
  pos bigint;
  prev text;
  r audit.audit_event;
  h text;
  n int := 0;
begin
  if t is null then
    raise exception 'tenant context required';
  end if;
  perform pg_advisory_xact_lock(hashtextextended(t::text, 6008));

  select last_chain_pos, last_hash into pos, prev from audit.chain_state where tenant_id = t;
  if not found then
    pos := 0;
    prev := '';
    insert into audit.chain_state (tenant_id, last_chain_pos, last_hash) values (t, 0, '');
  end if;

  for r in
    select * from audit.audit_event
    where tenant_id = t and chain_pos is null
    order by occurred_at, id
    limit max_rows
  loop
    pos := pos + 1;
    h := encode(sha256(convert_to(prev || '|' || audit.canonical(r, pos), 'UTF8')), 'hex');
    update audit.audit_event
      set chain_pos = pos, prev_hash = prev, hash = h, sealed_at = now()
      where tenant_id = t and id = r.id and chain_pos is null;
    prev := h;
    n := n + 1;
  end loop;

  update audit.chain_state set last_chain_pos = pos, last_hash = prev where tenant_id = t;
  return n;
end;
$$;
grant execute on function audit.seal_pending(int) to tb_app;

-- Recomputes the current tenant's chain. Returns rows checked and the first broken position (null if intact).
-- Starts at the lowest remaining position, because retention purges the oldest sealed rows.
create function audit.verify_chain() returns table (checked bigint, first_break bigint, sealed_through bigint)
language plpgsql stable
set search_path = pg_catalog, audit, platform
as $$
declare
  r audit.audit_event;
  expected_pos bigint := null;
  prev text := null;
  cnt bigint := 0;
begin
  for r in
    select * from audit.audit_event
    where tenant_id = platform.current_tenant_id() and chain_pos is not null
    order by chain_pos
  loop
    if expected_pos is null then
      expected_pos := r.chain_pos;
      prev := r.prev_hash;
    end if;
    if r.chain_pos <> expected_pos
       or r.prev_hash is distinct from prev
       or r.hash <> encode(sha256(convert_to(prev || '|' || audit.canonical(r, r.chain_pos), 'UTF8')), 'hex') then
      return query select cnt, expected_pos, expected_pos - 1;
      return;
    end if;
    prev := r.hash;
    expected_pos := expected_pos + 1;
    cnt := cnt + 1;
  end loop;
  return query select cnt, null::bigint, coalesce(expected_pos - 1, 0);
end;
$$;
grant execute on function audit.verify_chain() to tb_app;

-- Retention purge for the current tenant (AUD-006). Deletes sealed rows older than the retention period
-- and records the purge itself. The application role cannot delete audit rows any other way.
create function audit.purge_expired(actor uuid, correlation text) returns bigint
language plpgsql security definer
set search_path = pg_catalog, audit, platform
as $$
declare
  t uuid := platform.current_tenant_id();
  keep_days int;
  cutoff timestamptz;
  removed bigint;
  min_pos bigint;
  max_pos bigint;
begin
  if t is null then
    raise exception 'tenant context required';
  end if;
  select days into keep_days from audit.retention where tenant_id = t;
  keep_days := coalesce(keep_days, 365);
  cutoff := now() - make_interval(days => keep_days);

  -- Only a contiguous prefix of the chain can be removed, so verification stays meaningful.
  select min(chain_pos), max(chain_pos) into min_pos, max_pos
  from audit.audit_event
  where tenant_id = t and chain_pos is not null
    and chain_pos < coalesce((select min(chain_pos) from audit.audit_event
                              where tenant_id = t and chain_pos is not null and occurred_at >= cutoff), 9223372036854775807);

  if min_pos is null then
    return 0;
  end if;

  delete from audit.audit_event where tenant_id = t and chain_pos between min_pos and max_pos;
  get diagnostics removed = row_count;

  insert into audit.audit_event (tenant_id, id, occurred_at, actor_id, actor_type, action, target_type, target_id, outcome, correlation_id, changes)
  values (t, gen_random_uuid(), clock_timestamp(), actor, case when actor is null then 'system' else 'user' end,
          'audit.purged', 'audit_stream', t::text, 'success', correlation,
          jsonb_build_object('count', removed, 'from_position', min_pos, 'to_position', max_pos, 'cutoff', cutoff, 'retention_days', keep_days));
  return removed;
end;
$$;
grant execute on function audit.purge_expired(uuid, text) to tb_app;
