-- Spec 017 FR-020: clock data imports match rows to people by employee number, under whichever source the tenant
-- chose for the field.
create index profile_employee_number_directory on people.profile (tenant_id, (directory_values->>'employeeNumber'));
create index profile_employee_number_admin on people.profile (tenant_id, (admin_values->>'employeeNumber'));
create index profile_employee_number_own on people.profile (tenant_id, (own_values->>'employeeNumber'));
