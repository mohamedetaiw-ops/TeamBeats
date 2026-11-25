-- Spec 035 R3 (T025): the public app gallery. The platform operator publishes apps after a security review, each named
-- by the HTTPS URL of its client ID metadata document with the name, publisher, description, redirect URIs and scopes
-- recorded at the review. A tenant's API administrator adds a published app, which creates the tenant's registration,
-- marked gallery, and the admin consent for its scopes; what the app is always comes from this list. A withdrawn entry
-- disables every tenant's registration of it at once. Platform data, no tenant column.
create table publicapi.gallery_app (
  client_id text primary key check (length(client_id) between 12 and 200 and client_id like 'https://%'),
  name text not null check (length(name) between 1 and 80),
  publisher text not null check (length(publisher) between 1 and 80),
  description text not null check (length(description) between 1 and 500),
  homepage text not null check (length(homepage) between 9 and 300 and homepage like 'https://%'),
  redirect_uris text[] not null,
  scopes text[] not null,
  status text not null default 'published' check (status in ('published', 'withdrawn')),
  reviewed_by uuid not null,
  reviewed_at timestamptz not null,
  created_at timestamptz not null default now()
);
grant select, insert, update on publicapi.gallery_app to tb_app;

alter table publicapi.app add column gallery boolean not null default false;
