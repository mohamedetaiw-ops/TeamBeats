-- Spec 026 R2 (FR-012): access reviews. Every 90 days each sponsor reviews their guests and each administrator's
-- manager reviews the administrator's role assignments; an item not confirmed within 14 days is removed.
create table security.access_review_schedule (
  tenant_id uuid not null primary key,
  next_at timestamptz not null
);
select platform.enable_tenant_rls('security.access_review_schedule');
grant select, insert, update on security.access_review_schedule to tb_app;

create table security.access_review (
  tenant_id uuid not null,
  id uuid not null,
  opened_at timestamptz not null,
  due_at timestamptz not null,
  closed_at timestamptz null,
  opened_by uuid null,
  primary key (tenant_id, id)
);
select platform.enable_tenant_rls('security.access_review');
grant select, insert, update on security.access_review to tb_app;
create unique index access_review_one_open on security.access_review (tenant_id) where closed_at is null;

-- Names are copied at opening so the reviewer sees what they review even after a space is renamed.
create table security.access_review_item (
  tenant_id uuid not null,
  id uuid not null,
  review_id uuid not null,
  kind text not null check (kind in ('guest', 'admin_role')),
  reviewer_id uuid not null,
  subject_id uuid not null,
  space_id uuid null,
  space_name text null,
  assignment_id uuid null,
  role_key text null,
  scope_type text null,
  scope_id uuid null,
  decision text null check (decision in ('confirmed', 'removed', 'expired', 'kept')),
  decided_by uuid null,
  decided_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, review_id) references security.access_review (tenant_id, id)
);
select platform.enable_tenant_rls('security.access_review_item');
grant select, insert, update on security.access_review_item to tb_app;
create index access_review_item_open on security.access_review_item (tenant_id, reviewer_id) where decision is null;
