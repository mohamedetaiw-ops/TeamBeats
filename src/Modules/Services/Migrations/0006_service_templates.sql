-- Spec 016 R2 T025: the starter template a service was made from, so a template is switched on once per tenant.
alter table services.service add column template_key text;
create unique index service_template_key on services.service (tenant_id, template_key) where template_key is not null;
