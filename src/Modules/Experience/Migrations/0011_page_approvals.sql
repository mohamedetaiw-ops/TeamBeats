-- Spec 032 R2 T023 (US6 scenario 1, FR-051): approval routes for pages. A route belongs to the whole tenant or to one
-- org unit and applies to the pages of that unit and the units below it, the nearest route winning. Where a route
-- applies, a page goes live only through a submission that an approver with page.approve (or a named approver)
-- decides in the spec 016 inbox; the submitter never decides their own change.
create table experience.page_route (
  tenant_id uuid not null,
  id uuid not null,
  org_unit_id uuid null,
  approver_ids uuid[] not null default '{}' check (cardinality(approver_ids) <= 20),
  updated_by uuid not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id)
);
create unique index page_route_scope on experience.page_route (tenant_id, coalesce(org_unit_id, '00000000-0000-0000-0000-000000000000'::uuid));
select platform.enable_tenant_rls('experience.page_route');
grant select, insert, update, delete on experience.page_route to tb_app;

-- A submission keeps the definition the editor sent, so the approver decides on exactly that and later draft edits
-- do not slip in. At most one submission of a page waits at a time.
create table experience.page_submission (
  tenant_id uuid not null,
  id uuid not null,
  page_id uuid not null,
  route_id uuid not null,
  revision int not null,
  definition jsonb not null,
  base_release int null,
  status text not null check (status in ('pending', 'approved', 'returned', 'withdrawn')),
  reason text null check (length(reason) <= 500),
  submitted_by uuid not null,
  submitted_at timestamptz not null,
  instance_id uuid null,
  decided_by uuid null,
  decided_at timestamptz null,
  comment text null check (length(comment) <= 2000),
  release int null,
  primary key (tenant_id, id),
  foreign key (tenant_id, page_id) references experience.page (tenant_id, id)
);
create unique index page_submission_pending on experience.page_submission (tenant_id, page_id) where status = 'pending';
create index page_submission_instance on experience.page_submission (tenant_id, instance_id);
select platform.enable_tenant_rls('experience.page_submission');
grant select, insert, update on experience.page_submission to tb_app;
