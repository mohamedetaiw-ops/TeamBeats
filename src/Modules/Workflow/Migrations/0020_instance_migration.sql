-- Spec 024 R3 T019: moving a running instance to another version re-links its message subscriptions to the target
-- version's element IDs, in place, so a waiting token keeps its subscription.
grant update on workflow.message_subscription to tb_app;
