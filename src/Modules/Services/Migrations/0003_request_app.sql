-- Spec 035 section 6 requests:submit (R2): an application app files a request for one named person. The person is the
-- requester, as if they had filed it, so they follow and cancel it as their own; the app that filed it is kept here.
alter table services.request add column requester_app_id uuid null;
create index request_app on services.request (tenant_id, requester_app_id, created_at desc) where requester_app_id is not null;
create index request_unit on services.request (tenant_id, requester_org_unit_id, created_at desc);
