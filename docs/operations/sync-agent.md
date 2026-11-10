# On-premises sync agent

The sync agent reads an on-premises Active Directory for TeamBeats (spec 017 US6, spec 003 US6). It runs inside the customer network, connects outbound only to the tenant's TeamBeats address over HTTPS, and reads the directory over LDAPS with a read-only service account. The account and its password stay on the agent's machine.

## Before you start

- In TeamBeats, an integration administrator adds an **On-premises Active Directory** connection in the Connections area: the domain controller host name, the LDAPS port (636, or 3269 for the global catalogue), the base DN and, if needed, LDAP filters for users and groups. Then they turn the connection on.
- A service account in AD with read access to the users and groups under the base DN. It needs no write rights.
- The machine running the agent trusts the domain controller's certificate (the agent never falls back to plain LDAP), can reach the domain controller on the LDAPS port, and can reach the tenant's TeamBeats address on port 443. No inbound port is opened.
- The machine's clock is kept in time (signed requests are refused when the clock is more than 5 minutes off).

## Enrol

1. In the connection's page, make an enrolment code. It is shown once, works once and expires after 24 hours.
2. On the agent machine run:

   ```
   TeamBeats.SyncAgent enrol --server https://<tenant address> --code <code> --name <agent name>
   ```

   The agent makes a key pair, sends only the public half with the code, and keeps the private key in its state folder (`%ProgramData%\TeamBeats\SyncAgent` on Windows, `/var/lib/teambeats-agent` on Linux). Keep that folder readable by the service account only.

## Run

Set the service account through settings or environment variables, never in a file that others can read:

- `TEAMBEATS_AGENT_Agent__LdapUser`, for example `svc-teambeats@corp.example`
- `TEAMBEATS_AGENT_Agent__LdapPassword`

Then run it as a service:

- Windows: `sc.exe create "TeamBeats Sync Agent" binPath= "<folder>\TeamBeats.SyncAgent.exe run"`, running as a dedicated account.
- Linux: a systemd unit with `Type=notify` and `ExecStart=/opt/teambeats-agent/TeamBeats.SyncAgent run`.
- Container: `deploy/docker/sync-agent.Dockerfile`, with a volume for the state folder.

The agent sends a heartbeat every minute and asks for work every 30 seconds. Directory sync runs on the schedule set in the directory settings; each run reads only what changed since the last one (`uSNChanged`), plus deleted users and groups.

## Revoke or replace

Revoke an agent from the connection's page. Its next request is refused, and any job it held goes back to the queue for another enrolled agent. To replace a machine, enrol a new agent with a new code, then revoke the old one. If every agent is quiet for 5 minutes, the connection shows as degraded and its owner is told.

## Limits

- A changed group is sent with all its members, so membership stays right even after an agent is replaced.
- Objects moved out of the base DN are not seen as removed until they are deleted or disabled.
