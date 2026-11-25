# Shared responsibility for customer-hosted installs

Spec 026 R3 (on-prem hardening pack). This matrix says who owns each security area when a customer runs TeamBeats in its own data centre or private cloud. Read it with the [hardening guide](hardening-guide.md), which says how the customer's part is done.

**TeamBeats** is the vendor: the product code, its releases and support. **Customer** is the IT team that runs the install. **Tenant administrator** is the person who configures a tenant in the product (Admin and Security pages). On a customer-hosted install, the customer and the tenant administrators are often the same organization, but the roles stay separate.

For the TeamBeats-hosted service, TeamBeats owns everything in the Customer column below except identity provider settings, tenant configuration and the customer's own users.

## Matrix

| Area | TeamBeats | Customer | Tenant administrator |
|---|---|---|---|
| Physical data centre, hardware, virtualization | | Owns | |
| Operating system and container runtime: patching, hardening | Names supported versions in release notes | Owns | |
| Base images (PostgreSQL, RabbitMQ, Gotenberg, Tika, ClamAV) | Names supported versions; ships the TeamBeats images | Patches and runs them | |
| Product code: secure development, code review, dependency scanning | Owns | | |
| Product security fixes | Builds and announces them, with a date | Applies them within the agreed window (guide section 3) | |
| Platform settings (`Platform:Hosting`, messaging, private addresses, environment) | Documents them; posture page checks them | Sets and keeps them | Sees the result on the posture page |
| Database: roles, TLS, row level security | Ships the roles script and tenant isolation (ADR-0002) | Creates roles, turns on TLS, never connects as a superuser | |
| Backups and restore, data residency of backups | | Owns, and tests restores | |
| Encryption at rest of volumes and backups | | Owns | |
| Encryption in transit to users | | Terminates TLS at the reverse proxy | |
| Platform keys and service secrets | Documents which keys exist | Generates, stores and rotates them in its secret manager | |
| Tenant secret references (`secret://tenants/...`) | Stops a tenant from using another tenant's references | Holds the values in its secret manager | Enters the references |
| Customer-managed encryption keys | Wraps and checks the tenant data key; blocks the tenant when the key is revoked | Runs the key service and keeps it reachable from the workers | Connects, changes and rotates the key |
| Network: segmentation, egress limits, load balancer | Works with no internet access when AI egress is blocked | Owns | |
| Identity provider and MFA | Supports OIDC and SAML; checks the MFA claim | Runs or contracts the identity provider | Sets the sign-in and security policy |
| User accounts, roles and access reviews | Central authorization, audit of every change | | Owns |
| Four-eyes approval of critical settings | Enforces it when it is on | | Turns it on and approves changes |
| Platform operator access (break-glass) | Audits it and shows it to the tenant | Limits the platform host to its operations network | Reviews it in the audit log |
| Logging and monitoring of containers | Writes metadata-only logs | Collects and keeps them | |
| Audit log and SIEM streaming | Records events and streams them | Runs the SIEM and its retention | Sets up the stream |
| Malware scanning of uploads | Scans every upload through ClamAV | Keeps ClamAV signatures current | |
| AI model providers | Routes every call through the AI Gateway; can block egress | Chooses and runs private model endpoints for air-gapped installs | Sets AI policy and budgets |
| Incident response | Handles product vulnerabilities and tells customers | Handles incidents in its environment | Reports suspicious activity |
| Certifications (ISO 27001, SOC 2, national) | Covers TeamBeats development and support processes | Covers the customer's own environment | |
| Decommissioning | | Exports data, destroys volumes and backups, revokes keys | Requests the export |

## How the posture page helps

With `Platform:Hosting=customer`, Security > Posture adds four installation checks that the customer owns: production mode, database TLS, durable messaging and private address blocking. A failed check names the setting in the hardening guide. The remaining Customer items in the matrix sit outside the product and cannot be checked from inside it; the customer keeps its own evidence for them.
