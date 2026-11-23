-- Spec 034 R2 T020 (US8 scenarios 2 and 3, FR-025): the human review queue. A scored production sample is flagged
-- for review when a metric scored low, the person said the answer did not help, the judge was unsure, or a policy
-- denied something. One review item per sample; any evaluation reviewer in scope of the agent's unit may decide it.
-- The judge's verdicts are kept with the sample (verdict and score; the reason only while the content is retained),
-- so a reviewer sees what was judged and the judge's uncertainty can be read later.
alter table evaluation.production_sample add column judge jsonb not null default '{}';
alter table evaluation.production_sample add column negative_feedback boolean not null default false;

create table evaluation.review_item (
  tenant_id uuid not null,
  id uuid not null,
  sample_id uuid not null,
  agent_id uuid not null,
  agent_version int not null,
  org_unit_id uuid not null,
  language text not null,
  reasons text[] not null,
  status text not null check (status in ('open', 'reviewed')),
  -- Per judged metric: the judge's verdict and the reviewer's, so overturned verdicts can be counted.
  verdicts jsonb not null default '{}',
  note text null check (note is null or length(note) <= 1000),
  reviewed_by uuid null,
  reviewed_at timestamptz null,
  promoted_dataset_id uuid null,
  promoted_version int null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, id)
);
create unique index review_item_sample on evaluation.review_item (tenant_id, sample_id);
create index review_item_open on evaluation.review_item (tenant_id, status, created_at);
create index review_item_agent on evaluation.review_item (tenant_id, agent_id, created_at desc);
select platform.enable_tenant_rls('evaluation.review_item');
grant select, insert, update, delete on evaluation.review_item to tb_app;

-- A case promoted from the review queue makes a new dataset version of its own kind.
alter table evaluation.dataset_version drop constraint dataset_version_source_check;
alter table evaluation.dataset_version add constraint dataset_version_source_check check (source in ('edit', 'import', 'review'));
