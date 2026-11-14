-- Spec 009 R2 US6: sites get a home page (a spec 032 page), the template they came from, an audience they are shared
-- with, and an archive record. Navigation and theme follow the site's org unit through specs 008 and 033.
alter table content.site
  add column home_page_id uuid null,
  add column template_key text null check (template_key is null or length(template_key) between 1 and 40),
  add column shared_audience jsonb null,
  add column archived_at timestamptz null,
  add column archived_by uuid null;
