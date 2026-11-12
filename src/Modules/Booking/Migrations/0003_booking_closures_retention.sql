-- Spec 031 (R2) slice 5: a site closed for a day (section 4 edge case) and the one year retention of bookings (section 8).

-- A day a facilities administrator closed a site, for an emergency or any reason the site's calendar does not know.
-- Nobody books that day, and the bookings there were cancelled with the reason when it was closed. Holidays come from
-- the site's working calendar (spec 022) and need no row here.
create table booking.site_closure (
  tenant_id uuid not null,
  site_id uuid not null,
  day date not null,
  reason text not null check (length(reason) between 1 and 500),
  created_by uuid not null,
  created_at timestamptz not null,
  primary key (tenant_id, site_id, day),
  foreign key (tenant_id, site_id) references booking.site (tenant_id, id)
);
select platform.enable_tenant_rls('booking.site_closure');
grant select, insert, delete on booking.site_closure to tb_app;

-- A booking that ended more than a year ago keeps only what counts need (section 8): who held it, who made it, who
-- decided it and the free text are cleared. holder_id and booked_by stay not null, so they become the empty id.
create index booking_retained on booking.booking (tenant_id, ends_at) where holder_id <> '00000000-0000-0000-0000-000000000000';
-- A no-show notice whose window and restriction are over a year past is deleted.
grant delete on booking.no_show_notice to tb_app;
