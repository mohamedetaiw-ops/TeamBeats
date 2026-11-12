-- Spec 029 US4, FR-007 and FR-008: guests invited by email. An invitation waits until the guest first signs in, then
-- becomes a guest member row with the same end date. Owners are told seven days before a guest's access ends.
create table spaces.guest_invitation (
  tenant_id uuid not null,
  id uuid not null,
  space_id uuid not null,
  email text not null,
  role text not null check (role in ('member', 'viewer')),
  status text not null check (status in ('pending', 'accepted', 'revoked', 'expired')),
  expires_at timestamptz not null,
  email_state text not null check (email_state in ('sent', 'retry', 'bounced', 'failed')),
  email_attempts int not null default 0,
  user_id uuid null,
  invited_by uuid not null,
  invited_at timestamptz not null,
  ended_at timestamptz null,
  primary key (tenant_id, id),
  foreign key (tenant_id, space_id) references spaces.space (tenant_id, id)
);
create unique index guest_invitation_pending on spaces.guest_invitation (tenant_id, space_id, email) where status = 'pending';
create index guest_invitation_email on spaces.guest_invitation (tenant_id, email) where status = 'pending';
select platform.enable_tenant_rls('spaces.guest_invitation');
grant select, insert, update, delete on spaces.guest_invitation to tb_app;

alter table spaces.space_member add column expiry_notice_at timestamptz null;
create index space_member_guest_expiry on spaces.space_member (tenant_id, expires_at) where guest;

-- The classified file gate (FR-008): guests open Confidential files only when the tenant's rule and the space's owner
-- both allow it. Restricted files are never open to guests (spec 026 FR-011).
alter table spaces.space add column guest_confidential boolean not null default false;
alter table spaces.setting add column guest_confidential boolean not null default false;
