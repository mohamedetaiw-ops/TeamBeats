# ADR-0016: Document source port and the SharePoint Online adapter

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-11-05 |
| Owners | Mohamed |
| Related specs | 011 (US9, US10, FR-023 to FR-033), 006 (US5, FR-010 to FR-015), planned 017 and 018; constitution VII, XVII, XXVII; ADR-0013 |

## Context and constraints
Spec 011 makes TeamBeats libraries the default document store, so tenants with no document management system get every feature without any connection. Many customers already keep files in SharePoint Online and want to see them in TeamBeats without moving them. Constitution XVII keeps external DTOs and auth flows inside connector adapters, constitution VII requires source permissions at query time for search and AI, and XXVII requires an ADR for a connector contract. ADR-0013 sets ports and adapters inside each module and puts connectors in the `Integrations` module.

## Decision drivers
Built-in store stays the default and is not weakened; SharePoint permissions are never widened; no Microsoft Graph types in `Documents`; the next document system (Google Drive, INT-004) is an adapter, not a rewrite; one Microsoft 365 connection per Entra tenant shared by all Microsoft features.

## Options considered
1. **Copy SharePoint files into TeamBeats storage on a schedule.** Simple reads, but two copies drift, permissions are copied rather than respected, and the customer's records stay in SharePoint anyway. Kept only as one-time import (011 US10).
2. **Connected libraries over a document source port, with a derived index and live permission checks (chosen).** SharePoint stays the system of record; TeamBeats keeps only the link, a derived index, mirrored permissions and a short-lived preview cache.
3. **SharePoint as the storage behind TeamBeats libraries.** Would let controlled document features run on SharePoint files, but TeamBeats would not own versions, so legal hold, retention and "version in force on a date" could not be guaranteed. Rejected for now.
4. **Let `Documents` call Microsoft Graph directly.** Fastest to build, breaks constitution XVII and ADR-0013. Rejected.

## Decision
Option 2.

1. **Port in `Documents`.** `Documents.Application` defines a document source port: list containers and items, get item with metadata and permissions, open content for a user, upload or new version for a user, changes since a cursor. The built-in store implements it inside `Documents`. Features that only list, preview, search or cite work the same over both sources; controlled document, retention, legal hold, watermark, guest sharing and recycle bin features stay on the built-in store only (011 FR-030).
2. **Canonical contract in `Integrations`.** `Integrations.Contracts` exposes a document repository contract with canonical types (container, item, version, principal, permission, change). A `Documents` adapter implements the port by calling it. `Documents` never references Graph types.
3. **SharePoint adapter in `Integrations`.** It holds the Microsoft Graph client, `Sites.Selected` application access for sync and permission reading on allowed sites, delegated on-behalf-of access for user actions, change notification subscriptions with per-subscription secrets, delta queries, throttling with Microsoft's retry advice, and mapping of Purview sensitivity labels to TeamBeats classifications.
4. **One connection, many features.** The Microsoft 365 connection of spec 006 FR-011 carries features (directory sync, SharePoint documents, Office editing, mail and calendar). Each feature declares its permissions and adds them by incremental consent.
5. **Permissions.** Search and AI filter by mirrored SharePoint permissions at query time through the `Search` module; open, preview, download and upload check live with the user's delegated access. Mirrored permissions may only narrow access.
6. **Sync.** Change notifications and delta queries run as durable jobs (constitution XVIII) with a daily full reconciliation; deletions and permission changes meet the KNO-006 objective.

## Consequences and risks
- Connected libraries depend on Microsoft Graph availability and throttling; degraded states (011 FR-031) and caching of listings cover outages, not live content.
- Users need a Microsoft account in the connected Entra tenant to see connected content; tenants signing in through another identity provider see a clear message.
- Two permission models side by side may confuse users; source badges and disabled actions with reasons make the difference visible.
- An on-premises SharePoint adapter is possible later through the same contract.

## Security and multi-tenancy implications
Connections, subscriptions, cursors, mirrored permissions and index entries are tenant-owned with RLS. Change notifications are matched to the tenant and subscription that created them and re-read from Graph, never trusted as payload. Credentials are secret manager references; delegated tokens are short-lived and never logged. Allowed sites are enforced by TeamBeats as well as by Microsoft consent.

## Rollout and migration plan
R1: built-in store implements the port from its first commit; Connections area with the Microsoft 365 connection for directory sync (spec 003). R2: `Integrations` module, document repository contract, SharePoint adapter, connected libraries and import. Architecture tests from ADR-0013 fail if `Documents` references any Microsoft Graph namespace.
