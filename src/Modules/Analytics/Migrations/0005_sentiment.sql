-- Spec 021 R2 T017 (US8, FR-013): sentiment of comments and survey free text. The tenant turns it on in its analytics
-- policy (off by default). Each text is classified once through the AI Gateway and only its label is kept, with the day,
-- the unit and location it counts in and its topic: never the text, never a person, never the source record's id.
alter table analytics.policy add column sentiment boolean not null default false;

-- Where each source left off, and why it last stopped when the gateway refused (shown on the dashboard).
create table analytics.sentiment_cursor (
  tenant_id uuid not null,
  source text not null,
  at timestamptz null,
  last_id uuid null,
  denial text null,
  updated_at timestamptz not null,
  primary key (tenant_id, source)
);
select platform.enable_tenant_rls('analytics.sentiment_cursor');
grant select, insert, update, delete on analytics.sentiment_cursor to tb_app;

create table analytics.sentiment (
  tenant_id uuid not null,
  seq bigint generated always as identity,
  source text not null,
  day date not null,
  org_unit_id uuid null,
  location text null,
  topic text not null check (length(topic) between 1 and 200),
  -- The author's pseudonymous key for a named text, to count distinct people; null for an anonymous one.
  contributor text null check (contributor is null or contributor ~ '^[0-9a-f]{32}$'),
  label text not null check (label in ('positive', 'neutral', 'negative')),
  classified_at timestamptz not null,
  primary key (tenant_id, seq)
);
create index sentiment_day on analytics.sentiment (tenant_id, day);
select platform.enable_tenant_rls('analytics.sentiment');
grant select, insert, delete on analytics.sentiment to tb_app;
