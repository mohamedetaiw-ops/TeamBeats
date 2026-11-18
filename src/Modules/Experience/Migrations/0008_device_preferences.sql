-- Spec 008 R2 T022 (FR-032, FR-036): device-specific choices. A personal layout belongs to every device ('any') or to one
-- device class, and each list of operations is checked on its own, so one device's order never makes another's invalid.
-- Device-specific preferences sit beside the cross-device values, by device class.
alter table experience.personal_layout add column device_class text not null default 'any'
  check (device_class in ('any', 'desktop', 'tablet', 'phone'));
alter table experience.personal_layout drop constraint personal_layout_pkey;
alter table experience.personal_layout add primary key (tenant_id, user_id, page_id, device_class);

alter table experience.preference_profile add column device_values jsonb not null default '{}'::jsonb;
