-- Spec 002 User Story 5: the organization mapping reads administrative units and organization attributes from the mirror.
alter table identity.directory_group add column kind text not null default 'group' check (kind in ('group', 'admin_unit'));
alter table identity.directory_connection add column admin_units_cursor text null;
