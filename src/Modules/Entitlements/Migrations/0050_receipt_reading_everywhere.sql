-- Spec 022 R3 FR-030: AI receipt reading is in every package. It still needs expense claims (hr.expenses), so a
-- Workplace tenant reads receipts once it has expenses through an add-on or an override, and each read counts
-- against the AI quota like any other gateway call.
update entitlements.package_capability pc
set enabled = true
from entitlements.package_version v
where pc.package_version_id = v.id
  and pc.capability_key = 'ai.receipt_reading'
  and v.package_key = 'workplace';
