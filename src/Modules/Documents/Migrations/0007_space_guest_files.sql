-- Spec 029 FR-008: whether guests of the space may open Confidential files in its library. Spaces sets it from the
-- tenant rule and the space owner's choice; Restricted files stay closed to guests whatever it says.
alter table documents.library add column guest_confidential boolean not null default false;
