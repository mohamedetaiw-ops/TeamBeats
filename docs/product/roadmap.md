# Release roadmap

Adopted 2026-11-04 (Mohamed). Replaces the M/S/L priority labels in the foundation: each requirement is now due in a named release, recorded in its spec's "Target release" field.

| Release | Foundation phases | Contents | Exit criteria |
|---|---|---|---|
| **R0 Internal alpha** | Phase 0 and 1 | Repo, CI, deployment skeleton. Tenant management, organization hierarchy, dev-only sign-in, authorization, entitlement core, audit and admin shell, localization framework (specs 001 to 007) | Cross-tenant and org boundary suites green; constitution checks automated in CI |
| **R1 Pilot** | Phase 2 and 3 (core) | Entra ID sign-in. Employee home, news and pages, directory and org chart, document library, search, AI Gateway with OpenRouter, cited AI assistant. Arabic and English complete | First design-partner tenant live; dev sign-in absent from production artifact |
| **R2 General availability** | Phase 3 (rest), 4 and 6 | Forms and workflows, Microsoft 365 and SharePoint, HR sync, notifications, quotas and metering, Google and SAML sign-in, analytics. French, Urdu and Hindi complete | Paid packages sellable; performance and penetration tests passed |
| **R3 Enterprise & Government** | Phase 5 | Agents with write tools, Oracle Fusion and SAP packs, dedicated and on-prem hardening, DR validation, SCIM | First on-prem government deployment |

Open: success measures per release (review finding B4).
