-- Spec 011 (R1) US4, FR-011: the knowledge base. Articles are content items of type knowledge (spec 009); this table
-- keeps the template each article started from and its related articles and documents. Feedback is one row per reader
-- and article, the latest answer, with the version it was given on.
create table documents.kb_article (
  tenant_id uuid not null,
  item_id uuid not null,
  template text not null check (template in ('sop', 'howto', 'faq', 'troubleshooting')),
  related_item_ids uuid[] not null default '{}',
  related_document_ids uuid[] not null default '{}',
  created_by uuid not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  primary key (tenant_id, item_id)
);
select platform.enable_tenant_rls('documents.kb_article');
grant select, insert, update on documents.kb_article to tb_app;

create table documents.kb_feedback (
  tenant_id uuid not null,
  item_id uuid not null,
  user_id uuid not null,
  helpful boolean not null,
  comment text null check (comment is null or length(comment) <= 1000),
  version int not null,
  updated_at timestamptz not null,
  primary key (tenant_id, item_id, user_id)
);
select platform.enable_tenant_rls('documents.kb_feedback');
grant select, insert, update on documents.kb_feedback to tb_app;
