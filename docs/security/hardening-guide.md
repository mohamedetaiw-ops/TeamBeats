# Hardening guide for customer-hosted installs

Spec 026 R3 (on-prem hardening pack). This guide is for the IT team that runs TeamBeats in its own data centre or private cloud. It lists what to set and check before go-live and on every upgrade. The posture page (Security > Posture) checks the items marked **self check** on the running install, once `Platform:Hosting` is set to `customer`. Who owns what is in the [shared responsibility matrix](shared-responsibility.md).

The product code is the same on every install (constitution, ADR-0001). Nothing in this guide changes product behaviour; it closes the gaps that only the operator of the install can close.

## 1. What runs

| Component | Image or build | Notes |
|---|---|---|
| API | `deploy/docker/api.Dockerfile` | Serves the REST API, the public API, the OAuth server and the MCP server. Stateless. |
| Workers | `deploy/docker/workers.Dockerfile` | Background jobs: outbox, sweeps, key checks, SIEM, analytics. Stateless. |
| Web | `deploy/docker/web.Dockerfile` | Next.js front end. Holds no tokens; forwards the session cookie to the API. |
| PostgreSQL 18 with pgvector | `pgvector/pgvector:pg18` | All product data. Row level security per tenant (ADR-0002). |
| RabbitMQ 4 | `rabbitmq:4.1-management` | Messages between API and workers (ADR-0006). |
| Gotenberg 8 | `gotenberg/gotenberg` | Office to PDF previews and letters, in its own container. |
| Apache Tika with Tesseract | `deploy/docker/tika-ocr.Dockerfile` | Text extraction and OCR, in its own container. |
| ClamAV | `clamav/clamav` | Malware scanning of uploads. |
| Sync agent (optional) | `deploy/docker/sync-agent.Dockerfile` | On-premises directory sync (`docs/operations/sync-agent.md`). |

Only the Web and API containers take traffic from users. Everything else stays on an internal network with no inbound route from users or the internet.

## 2. Before go-live

### Platform settings

| Setting | Value | Why |
|---|---|---|
| `Platform:Hosting` | `customer` | Turns on the installation checks on the posture page. |
| `ASPNETCORE_ENVIRONMENT` | `Production` | **Self check** (`install_production_mode`). Development pages, development sign-in and detailed errors exist only outside production. |
| `Auth:DevSignIn:Enabled` | `false` (the default) | **Self check** per tenant (`dev_sign_in_absent`). |
| `Messaging:Transport` | `RabbitMq` | **Self check** (`install_durable_messaging`). The in-memory transport loses queued work on restart and is for tests only. |
| `Integrations:Network:AllowPrivateAddresses` | `false`, unless needed | **Self check** (`install_private_addresses_blocked`). With `false`, connectors, webhooks, SIEM and key service calls cannot reach private, loopback or link-local addresses, which stops a tenant administrator from pointing a connector at internal systems. Set `true` only when an internal system must be reached, such as an internal Vault for customer-managed keys, and then limit the egress of the API and workers at the network layer to the hosts that need it. |
| `Http:TrustForwardedHeaders` | `true` only behind your reverse proxy | Client addresses come from the connection, or from the proxy's forwarded headers when this is on (spec 026 FR-002). Turn it on only when the API is reachable solely through your proxy, or network rules can be bypassed with a forged header. |
| `Ai:EgressBlocked` | `true` for air-gapped installs | Refuses every call to a hosted model provider; use private model endpoints (spec 013). |

### Database

- Create the three roles with `deploy/db/bootstrap.sql`: `tb_owner` owns the schemas and runs migrations, `tb_app` is what the API and workers use, `tb_operator` is for audited break-glass work. None of them is a superuser or has `BYPASSRLS`. Never connect the application as `postgres` or `tb_owner`: row level security would not hold.
- Require TLS between the application and the database: `SSL Mode=VerifyFull` in `ConnectionStrings:App`, with your CA. **Self check** (`install_database_tls`); a database on the same host through a Unix socket or loopback also passes.
- Encrypt the database volumes and backups at rest with keys you control. Customer-managed keys in the product (Security > Encryption key) add a tenant data key for the most sensitive content; they do not replace volume encryption.
- Back up with point-in-time recovery, keep backups in the same region as the data (spec 026 residency), and test a restore at least every quarter.
- Keep PostgreSQL patched to the latest minor release of 18.

### Secrets

- The product reads secrets from configuration (`ConfigurationSecretStore`). Fill them from your secret manager as environment variables or a mounted file at start; never put them in images, compose files or the repository.
- Tenant secrets are references (`secret://tenants/{tenant}/{name}`); a tenant can only use references under its own ID. Your secret manager must hold the value for each reference a tenant administrator enters.
- Generate the platform keys with a cryptographic random generator, 32 bytes, base64: `Ai:DebugLog:Key`, `Integrations:Microsoft365:TokenKey`, `Mobile:OfflineKey`, `PublicApi:SigningKey`. Keep each one in your secret manager and rotate it on a schedule you set. Rotating `Integrations:Microsoft365:TokenKey` signs people out of Outlook features until they connect again.
- RabbitMQ: replace the default user, give the application its own user limited to the TeamBeats virtual host, and use TLS (`amqps`).

### Network

- Terminate TLS 1.2 or later (1.3 preferred) at your reverse proxy with a certificate from your CA. Redirect HTTP to HTTPS and send HSTS.
- Expose only the Web and API through the proxy. Keep PostgreSQL, RabbitMQ, Gotenberg, Tika and ClamAV on internal networks reachable only from the API and workers.
- Allow outbound traffic only to what the tenants use: their identity provider, Microsoft Graph, their connectors, their SIEM and their key service. Everything else can stay closed; the product works with no internet access when AI egress is blocked and no cloud connectors are set up.
- The `/health/live` endpoint is for your load balancer and monitoring only.

### Identity

- Each tenant signs in through its own identity provider (spec 003). Require MFA at the identity provider and turn on "Require MFA" in the tenant's security policy once a test sign-in shows the claim.
- Keep the platform operator host (`platform.` subdomain) reachable only from your operations network. Platform operators reach tenant data only through break-glass, which each tenant sees in its audit log and posture page.

## 3. Operating

- **Patching:** apply TeamBeats releases within 30 days, and security releases within 7 days or by the date in the security notice. Patch the base images, PostgreSQL, RabbitMQ and the operating system on your own cycle; the release notes name the supported versions.
- **Monitoring:** collect the logs of every container. Product logs hold metadata only, never message content or secrets. Stream tenant audit and security events to your SIEM from Security > SIEM.
- **Posture:** review Security > Posture after every change of configuration and at least monthly. Every failed installation check names the setting to fix in this guide.
- **Keys:** if a tenant uses customer-managed keys, the key check runs every 5 minutes from the workers. Keep the key service reachable from the workers; after 24 hours without an answer a key counts as revoked and the tenant becomes unavailable.
- **Incident response:** follow your own process. The audit log (spec 006) and the SIEM stream are the product's evidence. Raise a security incident with TeamBeats support for anything that may be a product vulnerability.
- **Decommissioning:** export tenant data before removing an install, then destroy the database volumes, backups and object storage, and revoke the platform keys and any customer-managed keys.

## 4. Upgrade checklist

1. Read the release notes for migrations and configuration changes.
2. Take a backup and confirm it restores.
3. Run migrations as `tb_owner`; start the API and workers as `tb_app`.
4. Open Security > Posture on one tenant and confirm every installation check still passes.
5. Watch the logs and the health endpoint for one hour.
