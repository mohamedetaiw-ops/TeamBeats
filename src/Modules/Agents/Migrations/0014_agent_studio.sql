-- Spec 020 R2 (US4): a clone remembers the agent it came from. Customer-created agents are those with no template.
alter table agents.agent add column cloned_from uuid null;
