-- Spec 020 FR-005: forced security updates of platform templates. The highest update version announced to the agent's
-- owners and the highest applied (audited and told), so each is sent once.
alter table agents.agent add column template_security_announced int not null default 0;
alter table agents.agent add column template_security_applied int not null default 0;
