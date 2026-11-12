-- Spec 030 US3: what a device may keep for reading with no connection, set by the security administrator.
alter table security.policy add column offline_max_items int not null default 50 check (offline_max_items between 0 and 200);
alter table security.policy add column offline_max_mb int not null default 50 check (offline_max_mb between 1 and 200);
alter table security.policy add column offline_days int not null default 7 check (offline_days between 1 and 30);
