-- Spec 021 R3: benchmarks across anonymized tenants with consent (section 7, open question default opt in only).
-- A tenant that opts in gives one figure per metric and week, already aggregated for the whole tenant. The figures sit
-- in analytics.benchmark_contribution, which no tenant can read (docs/architecture/rls-exceptions.md): tb_app has no
-- privilege on it, rows change only through analytics.contribute_benchmark and analytics.withdraw_benchmarks under the
-- caller's own tenant context, and analytics.benchmark_figures returns only the number of tenants and quartiles, and
-- nothing at all for fewer than five tenants.
alter table analytics.policy add column benchmarks boolean not null default false;
alter table analytics.policy add column benchmarks_week date null;

create table analytics.benchmark_contribution (
  tenant_id uuid not null,
  metric text not null,
  week date not null,
  value numeric not null check (value >= 0),
  primary key (tenant_id, metric, week)
);
revoke all on analytics.benchmark_contribution from public;

-- Replaces the current tenant's figure for a metric and week, only while its policy has benchmarks on.
create function analytics.contribute_benchmark(p_metric text, p_week date, p_value numeric) returns boolean
language plpgsql security definer
set search_path = pg_catalog, analytics, platform
as $$
declare
  t uuid := platform.current_tenant_id();
begin
  if t is null then
    raise exception 'tenant context required';
  end if;
  delete from analytics.benchmark_contribution where tenant_id = t and metric = p_metric and week = p_week;
  if p_value is null or not exists (select 1 from analytics.policy where tenant_id = t and benchmarks) then
    return false;
  end if;
  insert into analytics.benchmark_contribution (tenant_id, metric, week, value) values (t, p_metric, p_week, p_value);
  return true;
end;
$$;
revoke all on function analytics.contribute_benchmark(text, date, numeric) from public;
grant execute on function analytics.contribute_benchmark(text, date, numeric) to tb_app;

-- Removes every figure the current tenant gave, when it opts out.
create function analytics.withdraw_benchmarks() returns int
language plpgsql security definer
set search_path = pg_catalog, analytics, platform
as $$
declare
  t uuid := platform.current_tenant_id();
  n int;
begin
  if t is null then
    raise exception 'tenant context required';
  end if;
  delete from analytics.benchmark_contribution where tenant_id = t;
  get diagnostics n = row_count;
  return n;
end;
$$;
revoke all on function analytics.withdraw_benchmarks() from public;
grant execute on function analytics.withdraw_benchmarks() to tb_app;

-- The quartiles of a metric in a week across the tenants that gave a figure, for a caller whose own tenant opted in.
-- Fewer than five tenants returns the count only, so no tenant can be worked out from the others.
create function analytics.benchmark_figures(p_metric text, p_week date)
returns table (tenants int, p25 numeric, p50 numeric, p75 numeric)
language plpgsql security definer
set search_path = pg_catalog, analytics, platform
as $$
declare
  t uuid := platform.current_tenant_id();
begin
  if t is null then
    raise exception 'tenant context required';
  end if;
  if not exists (select 1 from analytics.policy where tenant_id = t and benchmarks) then
    return;
  end if;
  return query
  select count(*)::int,
    case when count(*) >= 5 then round(percentile_cont(0.25) within group (order by c.value)::numeric, 1) end,
    case when count(*) >= 5 then round(percentile_cont(0.5) within group (order by c.value)::numeric, 1) end,
    case when count(*) >= 5 then round(percentile_cont(0.75) within group (order by c.value)::numeric, 1) end
  from analytics.benchmark_contribution c
  where c.metric = p_metric and c.week = p_week;
end;
$$;
revoke all on function analytics.benchmark_figures(text, date) from public;
grant execute on function analytics.benchmark_figures(text, date) to tb_app;
