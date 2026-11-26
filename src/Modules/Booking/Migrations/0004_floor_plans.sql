-- Spec 031 R3 US7 (FR-019 to FR-021): a floor plan image per floor, stored in object storage after the malware scan,
-- and each resource's place on it as percentages of the image's width and height.
alter table booking.floor
  add column plan_type text null check (plan_type in ('image/png', 'image/jpeg', 'image/webp')),
  add column plan_width int null check (plan_width between 1 and 8000),
  add column plan_height int null check (plan_height between 1 and 8000),
  add column plan_bytes int null,
  add column plan_version int not null default 0,
  add column plan_updated_by uuid null,
  add column plan_updated_at timestamptz null;

alter table booking.resource
  add column plan_x numeric(5, 2) null check (plan_x between 0 and 100),
  add column plan_y numeric(5, 2) null check (plan_y between 0 and 100),
  add constraint resource_plan_both check ((plan_x is null) = (plan_y is null));
