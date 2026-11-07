-- Spec 026 T010: a sign-in started for step-up asks the identity provider for a fresh authentication.
alter table identity.oidc_login_state add column step_up boolean not null default false;
