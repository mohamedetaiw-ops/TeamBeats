-- Spec 003 R2 (US4, US5): Google Workspace and AD FS sign-in over OIDC on the same provider model as Entra ID.
-- Google signs in any Google account under one shared issuer, so a Google provider names the hosted domains that
-- belong to this tenant and the token's hd claim must be one of them. An AD FS provider is tied to the tenant's AD DS
-- directory connection (the Integrations connection the sync reads, checked when saved): its objectGUID claim links to
-- the users that connection synced, never to another's.
-- The last refused token's reason is kept so the admin sees which claim rule is missing (US5 scenario 2).
alter table identity.idp_config drop constraint idp_config_kind_check;
alter table identity.idp_config add constraint idp_config_kind_check check (kind in ('entra', 'google', 'adfs', 'generic'));

alter table identity.idp_config
  add column hosted_domains text[] not null default '{}',
  add column directory_connection_id uuid null,
  add column last_problem text null check (last_problem is null or length(last_problem) <= 64),
  add column last_problem_at timestamptz null,
  add constraint idp_config_adfs_connection check ((kind = 'adfs') = (directory_connection_id is not null)),
  add constraint idp_config_google_domains check (kind <> 'google' or cardinality(hosted_domains) > 0);
