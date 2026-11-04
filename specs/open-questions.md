# Open questions for specs 001 to 007

> Status 2026-11-04: Mohamed accepted all defaults below. They are now written into the specs, which are Approved. C1 to C3 stay open as commercial data and do not block implementation.

Each line has a safe default already written into the spec. Reply "accept defaults" to take them all, or name the numbers you want to change. Commercial numbers (C1 to C3) do not block approval because entitlement values are data and can change without code.

| # | Spec | Question | Default |
|---|---|---|---|
| 1 | 001 | Allowed logo types and size | PNG, JPEG, sanitized SVG; max 1 MB |
| 2 | 001 | Data retention after a tenant is closed | 90 days, configurable per contract |
| 3 | 001 | Tenant URL style | Subdomain in shared installs, single host in dedicated installs |
| 4 | 002 | CSV import of the org hierarchy | R1, not R0 |
| 5 | 002 | Maximum hierarchy depth | 12 levels |
| 6 | 002 | Design-to size | 20,000 org units per tenant |
| 7 | 002 | Rules on which unit type can sit under which | None, warning only |
| 8 | 003 | Session cut-off after a user is disabled / roles change | Immediate / within 5 minutes |
| 9 | 003 | SAML sign-in package tier | All packages |
| 10 | 004 | Explicit "deny" permissions | Not in R0; restrict by narrowing grants |
| 11 | 004 | Time for permission changes to take effect | 60 seconds |
| 12 | 004 | Can a tenant remove default employee access (for example contractors) | Yes, per membership type |
| 13 | 004 | Custom roles package tier | Workplace Plus and above |
| 14 | 005 | Quota overshoot tolerance under concurrency | 1% or 10 units, whichever is larger |
| 15 | 005 | What happens when a subscription expires | 30-day grace, then read-only |
| 16 | 006 | Minimum audit retention | 365 days |
| 17 | 006 | Legal hold | R2 |
| 18 | 006 | SIEM export | Later release, JSON over HTTPS or syslog |
| 19 | 007 | Arabic digits and calendar | Western digits and Gregorian by default, tenant can switch digits; Hijri as optional secondary date later |
| 20 | 007 | Urdu script style | Nastaliq for content, with a performance test |
| C1 | 005 | Capability matrix per package | Placeholder table in spec 005, mirrors foundation section 4 |
| C2 | 005 | AI request quotas per package | To be set with a cost model per active user |
| C3 | 002 | Max org units per package | To be set commercially |
