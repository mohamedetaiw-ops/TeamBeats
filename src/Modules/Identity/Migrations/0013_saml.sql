-- Spec 003 R2 (US4, US5; IAM-006): SAML 2.0 sign-in on the same provider row. A SAML provider names the IdP's entity
-- ID (issuer), its single sign-on address and up to three signing certificates (public, base64 DER; never a secret).
-- It has no client ID or secret. Pending SAML requests reuse identity.oidc_login_state: the request ID sits in nonce
-- and code_verifier stays empty, so an OIDC callback can never consume a SAML request or the other way round.
alter table identity.idp_config
  add column sso_url text null check (sso_url is null or (sso_url like 'https://%' and length(sso_url) <= 2000)),
  add column saml_certificates text[] not null default '{}',
  add constraint idp_config_saml_fields check (protocol <> 'saml' or (sso_url is not null and cardinality(saml_certificates) between 1 and 3)),
  add constraint idp_config_saml_kinds check (protocol <> 'saml' or kind in ('generic', 'adfs'));
