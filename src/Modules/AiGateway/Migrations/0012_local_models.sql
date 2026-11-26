-- Spec 013 R3 (FR-021): models an install hosts in its own network are the catalogue provider 'local'. Only they may
-- take Restricted content, because it never leaves the customer's network; shared providers stay at Confidential at most.
alter table ai.model_catalogue drop constraint model_catalogue_provider_check;
alter table ai.model_catalogue add constraint model_catalogue_provider_check check (provider in ('openrouter', 'local'));
alter table ai.model_catalogue drop constraint model_catalogue_max_classification_check;
alter table ai.model_catalogue add constraint model_catalogue_max_classification_check
  check (max_classification in ('public', 'internal', 'confidential') or (max_classification = 'restricted' and provider = 'local'));
