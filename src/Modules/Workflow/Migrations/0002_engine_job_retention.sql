-- Done engine jobs are kept 30 days, then deleted by the job worker (spec 024 data table).
grant delete on workflow.engine_job to tb_app;
create index engine_job_task on workflow.engine_job (tenant_id, task_id) where done_at is null;
