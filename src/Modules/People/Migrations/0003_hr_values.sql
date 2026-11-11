-- Spec 019 FR-003: values from the HR system (spec 019 US1 scenario 4) are kept apart from directory, own and
-- administrator values, and a field or a reporting line may have HR as its source.
alter table people.profile add column hr_values jsonb not null default '{}'::jsonb;
create index profile_employee_number_hr on people.profile (tenant_id, (hr_values->>'employeeNumber'));

alter table people.field_policy drop constraint field_policy_source_check;
alter table people.field_policy add constraint field_policy_source_check check (source in ('directory', 'person', 'admin', 'hr'));

alter table people.reporting_line drop constraint reporting_line_source_check;
alter table people.reporting_line add constraint reporting_line_source_check check (source in ('directory', 'manual', 'hr'));
