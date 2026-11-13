-- Spec 016 R2 US6: a queue member picks (claims) a task so the rest of the queue stops seeing it; the person who picked
-- it may put it back. A claim lapses when that person is no longer a candidate.
alter table workflow.user_task add column claimed_by uuid null;
alter table workflow.user_task add column claimed_at timestamptz null;
