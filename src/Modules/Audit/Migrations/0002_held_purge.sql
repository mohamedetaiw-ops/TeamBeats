-- Spec 011 R2 US7 scenario 5 and FR-015: audit records selected by a legal hold are kept until the hold is released.
-- The caller passes the chain position of the earliest held record; the purge removes only the prefix before it, so the
-- chain stays verifiable and every held record stays.
create function audit.purge_expired(actor uuid, correlation text, keep_from bigint) returns bigint
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
                              where tenant_id = t and chain_pos is not null and occurred_at >= cutoff), 9223372036854775807)
    and chain_pos < coalesce(keep_from, 9223372036854775807);

  if min_pos is null then
    return 0;
  end if;

  delete from audit.audit_event where tenant_id = t and chain_pos between min_pos and max_pos;
  get diagnostics removed = row_count;

  insert into audit.audit_event (tenant_id, id, occurred_at, actor_id, actor_type, action, target_type, target_id, outcome, correlation_id, changes)
  values (t, gen_random_uuid(), clock_timestamp(), actor, case when actor is null then 'system' else 'user' end,
          'audit.purged', 'audit_stream', t::text, 'success', correlation,
          jsonb_build_object('count', removed, 'from_position', min_pos, 'to_position', max_pos, 'cutoff', cutoff, 'retention_days', keep_days,
            'held_from_position', keep_from));
  return removed;
end;
$$;
grant execute on function audit.purge_expired(uuid, text, bigint) to tb_app;
