-- Spec 010 T012 (US3 scenario 3): a person who has not uploaded a photo shows their directory photo. photo_source says
-- where the current photo came from; directory_photo_tag is the directory's tag of the last directory photo applied or
-- removed, so a removed directory photo does not come back until the directory has a new one; directory_photo_checked_at
-- spreads the photo reads over the syncs.
alter table people.profile add column photo_source text null check (photo_source in ('own', 'directory'));
alter table people.profile add column directory_photo_tag text null;
alter table people.profile add column directory_photo_checked_at timestamptz null;

-- Photos so far were all uploads; one tenant at a time under forced RLS (see the authorization migration 0007).
do $$
declare
  tenants uuid[];
  tenant uuid;
begin
  alter table people.profile no force row level security;
  select coalesce(array_agg(distinct tenant_id), '{}') into tenants from people.profile where photo_version is not null;
  alter table people.profile force row level security;

  foreach tenant in array tenants loop
    perform set_config('app.tenant_id', tenant::text, true);
    update people.profile set photo_source = 'own' where photo_version is not null;
  end loop;
  perform set_config('app.tenant_id', '', true);
end $$;
