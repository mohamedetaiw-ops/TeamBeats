-- Spec 016 R2 on the spec 024 engine: the result an end event names (tb:result), so the module that owns the process
-- learns how it ended (a request approved or rejected). Escalation jobs reuse workflow.engine_job (kind task_escalate).
alter table workflow.process_instance add column result text null check (length(result) <= 63);
