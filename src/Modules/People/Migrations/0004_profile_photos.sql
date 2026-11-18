-- Spec 010 FR-013: the version of the person's photo. The resized files live in object storage under the tenant's
-- path, named by this version; null means the profile shows initials.
alter table people.profile add column photo_version int null;
alter table people.profile add column photo_updated_at timestamptz null;
