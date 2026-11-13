-- Spec 035 FR-011 and spec 004 FR-017 (R2): application principals. An application app holds roles as the subject
-- type 'app' (its ID is the app's ID), and only roles marked assignable to apps. Such a role can never hold a
-- permission for access, identity, security or API administration, audit export or AI policy: the trigger below
-- refuses it whichever way it is attempted. The built-in App reader role is added to tenants that already exist, one
-- tenant at a time (see 0007); new tenants get it from the seed.
alter table authz.role add column assignable_to_apps boolean not null default false;

alter table authz.role_assignment drop constraint role_assignment_subject_type_check;
alter table authz.role_assignment add constraint role_assignment_subject_type_check check (subject_type in ('user', 'group', 'app'));

create function authz.excluded_from_apps(permission text) returns boolean
language sql immutable as $$
  select permission like 'authz.%' or permission like 'identity.%' or permission like 'security.%' or permission like 'api.%'
      or permission in ('audit.export', 'ai.policy_manage')
$$;

create function authz.check_app_assignable() returns trigger
language plpgsql as $$
begin
  if tg_table_name = 'role_permission' then
    if authz.excluded_from_apps(new.permission_key)
       and exists (select 1 from authz.role r where r.tenant_id = new.tenant_id and r.id = new.role_id and r.assignable_to_apps) then
      raise exception 'permission % cannot be in a role assignable to apps', new.permission_key using errcode = 'check_violation';
    end if;
  elsif new.assignable_to_apps
        and exists (select 1 from authz.role_permission p where p.tenant_id = new.tenant_id and p.role_id = new.id and authz.excluded_from_apps(p.permission_key)) then
    raise exception 'role % holds a permission an app cannot hold', new.key using errcode = 'check_violation';
  end if;
  return new;
end $$;

create trigger role_permission_app_assignable before insert or update on authz.role_permission
  for each row execute function authz.check_app_assignable();
create trigger role_app_assignable before insert or update of assignable_to_apps on authz.role
  for each row execute function authz.check_app_assignable();

-- An app holds a role only while the role stays assignable to apps.
create function authz.check_app_assignment() returns trigger
language plpgsql as $$
begin
  if new.subject_type = 'app' and new.revoked_at is null
     and not exists (select 1 from authz.role r where r.tenant_id = new.tenant_id and r.id = new.role_id and r.assignable_to_apps) then
    raise exception 'role is not assignable to apps' using errcode = 'check_violation';
  end if;
  return new;
end $$;

create trigger role_assignment_app before insert on authz.role_assignment
  for each row execute function authz.check_app_assignment();

do $$
declare
  tenants uuid[];
  tenant uuid;
begin
  alter table authz.role no force row level security;
  select coalesce(array_agg(distinct tenant_id), '{}') into tenants from authz.role;
  alter table authz.role force row level security;

  foreach tenant in array tenants loop
    perform set_config('app.tenant_id', tenant::text, true);
    insert into authz.role (tenant_id, id, key, is_builtin, is_delegable, is_delegated_admin, assignable_to_apps)
    select r.tenant_id, gen_random_uuid(), 'app_reader', true, true, false, true
    from authz.role r
    where r.key = 'tenant_administrator' and r.is_builtin
    on conflict (tenant_id, key) do nothing;

    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    cross join (values ('org.view'), ('page.view'), ('content.view'), ('doc.view'), ('people.view')) as p(permission)
    where r.key = 'app_reader' and r.is_builtin
    on conflict do nothing;
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
