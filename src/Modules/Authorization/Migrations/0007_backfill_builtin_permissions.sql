-- Migrations 0003 to 0006 meant to add new built-in roles and permissions to tenants that already existed, but authz.role
-- and authz.role_permission force row-level security, so without a tenant set those statements saw no rows and changed
-- nothing. Tenants created before them (such as the staging tenant) kept administrators without page, theme, shell and
-- personalization permissions. This applies the same additions once per tenant, with the tenant set. It is idempotent.
do $$
declare
  tenants uuid[];
  tenant uuid;
begin
  -- The module's own tables name the tenants: row-level security is lifted for this one read, inside this transaction.
  alter table authz.role no force row level security;
  select coalesce(array_agg(distinct tenant_id), '{}') into tenants from authz.role;
  alter table authz.role force row level security;

  foreach tenant in array tenants loop
    perform set_config('app.tenant_id', tenant::text, true);

    insert into authz.role (tenant_id, id, key, is_builtin, is_delegable, is_delegated_admin)
    select r.tenant_id, gen_random_uuid(), k.key, true, true, false
    from authz.role r cross join (values ('page_editor'), ('page_publisher'), ('brand_manager')) as k(key)
    where r.key = 'tenant_administrator' and r.is_builtin
    on conflict (tenant_id, key) do nothing;

    insert into authz.role_permission (tenant_id, role_id, permission_key)
    select r.tenant_id, r.id, p.permission
    from authz.role r
    join (values
      -- 0003, spec 032
      ('tenant_administrator', 'page.view'), ('tenant_administrator', 'page.edit'), ('tenant_administrator', 'page.publish'),
      ('tenant_administrator', 'template.edit'), ('tenant_administrator', 'template.distribute'), ('tenant_administrator', 'component.enable'),
      ('tenant_administrator', 'config_history.view'),
      ('organization_administrator', 'page.view'), ('organization_administrator', 'page.edit'), ('organization_administrator', 'page.publish'),
      ('organization_administrator', 'template.edit'),
      ('page_editor', 'org.view'), ('page_editor', 'page.view'), ('page_editor', 'page.edit'),
      ('page_publisher', 'org.view'), ('page_publisher', 'page.view'), ('page_publisher', 'page.edit'), ('page_publisher', 'page.publish'),
      ('security_officer', 'config_history.view'),
      ('employee', 'page.view'),
      -- 0004, spec 033
      ('tenant_administrator', 'theme.edit'), ('tenant_administrator', 'theme.publish'), ('tenant_administrator', 'config_lock.manage'),
      ('organization_administrator', 'theme.edit'), ('organization_administrator', 'theme.publish'),
      ('brand_manager', 'org.view'), ('brand_manager', 'theme.edit'),
      -- 0005, spec 008
      ('tenant_administrator', 'shell.edit'), ('tenant_administrator', 'navigation.edit'), ('tenant_administrator', 'help.edit'),
      ('organization_administrator', 'navigation.edit'),
      -- 0006, spec 008
      ('tenant_administrator', 'personalization.policy'), ('tenant_administrator', 'personal_layout.edit'),
      ('organization_administrator', 'personalization.policy'),
      ('employee', 'personal_layout.edit')
    ) as p(role_key, permission) on p.role_key = r.key
    where r.is_builtin
    on conflict do nothing;

    -- Cached grants are reloaded at their next check.
    update authz.grants_version set version = version + 1;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
