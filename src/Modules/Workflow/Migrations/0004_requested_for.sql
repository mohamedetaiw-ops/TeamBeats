-- The person an instance is for, when someone else started it (spec 016: a request filed on behalf of a colleague).
-- Line manager steps resolve against this person; null means the starter.
alter table workflow.process_instance add column requested_for uuid;
