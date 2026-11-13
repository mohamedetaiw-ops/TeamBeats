-- Spec 026 R2 (US3, section 14): the tenant's guest domain rules. Spaces checks them before an invitation is sent.
-- Domains are lower case; an entry covers its subdomains.
alter table security.policy add column guest_allow_domains text[] not null default '{}';
alter table security.policy add column guest_block_domains text[] not null default '{}';
