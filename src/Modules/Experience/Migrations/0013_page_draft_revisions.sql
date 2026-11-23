-- Spec 032 R2 T026 (US6 scenario 6, FR-042, FR-043): earlier draft versions. Every replacement of a page draft keeps the
-- version it replaces, so overwriting someone's change after a conflict loses nothing, a merge has the common version
-- to compare against, and editors can go back. The last 50 versions of each page are kept.
create table experience.page_draft_revision (
  tenant_id uuid not null,
  page_id uuid not null,
  revision int not null,
  definition jsonb not null,
  updated_by uuid not null,
  updated_at timestamptz not null,
  primary key (tenant_id, page_id, revision),
  foreign key (tenant_id, page_id) references experience.page (tenant_id, id)
);
select platform.enable_tenant_rls('experience.page_draft_revision');
grant select, insert, delete on experience.page_draft_revision to tb_app;

create function experience.keep_draft_revision() returns trigger language plpgsql as $$
begin
  if new.revision <> old.revision then
    insert into experience.page_draft_revision (tenant_id, page_id, revision, definition, updated_by, updated_at)
    values (old.tenant_id, old.page_id, old.revision, old.definition, old.updated_by, old.updated_at)
    on conflict do nothing;
    delete from experience.page_draft_revision
    where tenant_id = old.tenant_id and page_id = old.page_id and revision <= old.revision - 50;
  end if;
  return new;
end;
$$;

create trigger page_draft_keep_revision before update on experience.page_draft
  for each row execute function experience.keep_draft_revision();
