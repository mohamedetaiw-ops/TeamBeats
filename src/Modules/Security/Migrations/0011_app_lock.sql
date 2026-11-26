-- Spec 030 R3 (MOB-023): the app lock of the store apps is part of the tenant security policy. Off, optional (the
-- person turns it on) or required, after 0 to 60 minutes in the background; optional after 5 minutes by default.
alter table security.policy add column app_lock text not null default 'optional' check (app_lock in ('off', 'optional', 'required'));
alter table security.policy add column app_lock_minutes int not null default 5 check (app_lock_minutes between 0 and 60);
