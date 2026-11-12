-- Spec 023 (R2) slice 8: celebrations (US6, FR-015, FR-016). Birthdays are opt-in and keep the day and month only,
-- never the year. Work anniversaries and joiners are read from people (010) through its contract, never stored here.
-- Every table is tenant-owned with forced row-level security.

-- A person's celebration choices. The birthday columns are both set or both null; opting out clears them.
create table engagement.celebration_preference (
  tenant_id uuid not null,
  user_id uuid not null,
  birthday_month int null check (birthday_month between 1 and 12),
  birthday_day int null check (birthday_day between 1 and 31),
  anniversary_opt_out boolean not null default false,
  updated_at timestamptz not null,
  primary key (tenant_id, user_id),
  check ((birthday_month is null) = (birthday_day is null))
);
create index celebration_birthday on engagement.celebration_preference (tenant_id, birthday_month, birthday_day) where birthday_month is not null;
select platform.enable_tenant_rls('engagement.celebration_preference');
grant select, insert, update on engagement.celebration_preference to tb_app;

-- The tenant's celebration settings (versioned, changed with If-Match). No row means the defaults.
create table engagement.celebration_setting (
  tenant_id uuid not null primary key,
  birthdays boolean not null,
  anniversaries boolean not null,
  joiners boolean not null,
  version int not null default 1,
  updated_by uuid not null,
  updated_at timestamptz not null
);
select platform.enable_tenant_rls('engagement.celebration_setting');
grant select, insert, update on engagement.celebration_setting to tb_app;
