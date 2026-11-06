# Fake external systems and fictional test data

Status: in use (test infrastructure; follows ADR-0004, spec 003 US7, CLAUDE.md rules 4 and 9)
Date: 2026-11-06

## 1. Why
Local development, the Playwright suite and the shared staging deployment must exercise every R1 feature without real Microsoft 365 tenants, mailboxes or AI provider accounts. Staging holds fictional data only and never connects to a real directory (spec 003 US7). The fakes make that true by construction.

## 2. How it works
The real adapters run unchanged. Only their outbound HTTP clients are pointed at in-process fakes, and browser redirects to Microsoft are rewritten to fake pages on the same host.

| External system | Real adapter (unchanged) | Fake (`src/DevFakes`) | What the fake does |
|---|---|---|---|
| Microsoft Entra ID sign-in (spec 003 US2) | `HttpOidcProvider`, HTTP client `oidc` | `FakeIdentityProvider` | Discovery, signing keys, token endpoint with client secret, redirect URI and PKCE checks. ID tokens carry `oid`, `email`, `name`, `groups`, `nonce`. |
| Microsoft admin consent (spec 006 US5) | `HttpMicrosoftGraph.AdminConsentUrl` | `/dev/fake/entra/{directory}/v2.0/adminconsent` | A consent page that lists the requested permissions; Accept returns `admin_consent=True`, Cancel returns `access_denied`. |
| Microsoft Graph directory (spec 003 US6, 002 US5) | `HttpMicrosoftGraph`, `MicrosoftDirectorySource`, HTTP client `msgraph` | `FakeMicrosoftGraph` | Client credentials tokens for consented directories, `$count`, `users/delta`, `groups/delta`, `administrativeUnits/delta` with paging and delta links. |
| Microsoft Graph mail, calendar, free/busy, rooms (spec 018) | to be built by the R1 build thread, HTTP client `msgraph` | `FakeMicrosoftGraph` | Delegated authorization code tokens (with PKCE), `GET /me`, `/me/messages`, `/me/mailFolders/inbox/messages`, `/me/calendarView`, `/me/events`, `POST /me/calendar/getSchedule`, `/places/microsoft.graph.room`. Scopes are enforced: no mail scope, no mailbox. |
| OpenRouter for the AI gateway (spec 013, ADR-0003) | to be built, HTTP client `openrouter` | `FakeOpenRouter` | `chat/completions` (plain and streaming), `models`, `embeddings`. Deterministic answer in the language of the question. Failure markers in the last user message: `[fake:429]`, `[fake:500]`, `[fake:timeout]`, `[fake:refuse]`. API key `fictional-openrouter-key`. |

The browser pages for sign-in and consent live under `/dev/fake/entra/...`. A middleware in the development host rewrites any redirect to `https://login.microsoftonline.com/...` into `/dev/fake/entra/...`, so the browser never leaves the test environment. The pages only send the browser back to the same host or another host under the installation's base domain (no open redirect). They render in Arabic or English from `Accept-Language`, right to left for Arabic, with a banner saying the data is fictional.

### Adding a fake for a new connector
When a new adapter calls an external system (SharePoint document source, a future connector in the Connections area):
1. Give the adapter a named HTTP client, as `oidc`, `msgraph` and `openrouter` are.
2. Add a fake `HttpMessageHandler` in `src/DevFakes` that speaks the external protocol, with no reference to any module.
3. Register it on the named client in `src/Api.DevHost/FakeSystemsSetup.cs` and, if it needs a browser step, redirect it with the same rewrite.
4. Add its data to the scenario file and an integration test in `tests/Integration/FakeSystemsTests.cs`.

## 3. Choosing per environment
| Environment | Host | Fakes | Scenario | Tenant |
|---|---|---|---|---|
| Development | `src/Api.DevHost` | on | `scenarios/staging-demo.json` | `demo` (open development sign-in, admin `admin@demo.example`) |
| Test (integration tests) | `ApiFactory` or `DevHostFactory` | the same fake classes, wired by the test factories | built per test | one per test |
| Playwright (local and CI) | `src/Api.DevHost` in Development | on | as Development | each test creates its own; `demo` is also there |
| Staging (Fly) | `src/Api.DevHost` in Staging | **must be on**, the host refuses to start otherwise | `scenarios/staging-demo.json` | `staging-demo` |
| Production | `src/Api` | **not present** | none | none |

Settings (`Dev:Fakes`): `Enabled`, `Scenario` (path relative to the content root or the published folder), `RunDirectorySync` (run due directory syncs inside the host, where the fake directory lives).

## 4. Guards
- `TeamBeats.DevFakes` is referenced only by `src/Api.DevHost` (architecture test `Fakes_are_referenced_only_by_the_development_host_and_tests`), never by `src/Api` or `src/Workers` (`Production_api_does_not_contain_development_sign_in`).
- The production API and workers refuse to start when any `Dev:Fakes` setting is present (`ProductionGuards`).
- CI's production artifact check fails when `TeamBeats.DevFakes.dll` is in the published API or workers; the production Dockerfiles check it too; `tools/deploy-lint.sh` fails on `DevFakes` or `Dev__Fakes` in a production manifest.
- With fakes on, `Integrations:Microsoft365:ClientId` may not name a real application; the host sets the fake application's fictional credentials itself.
- Staging fails to start with fakes off.
- Scenario files may only contain addresses on reserved `.example` domains (loader check and architecture test).
- Workers in staging run the production image, which has no fakes. Set `Workers__DirectorySync__Enabled=false` there; the staging API host runs the sync instead.

## 5. The `staging-demo` scenario
`src/Api.DevHost/scenarios/staging-demo.json`, all fictional:
- **Directory** "TeamBeats Demo Group (fake Entra ID)", `5a7e0c3d-1f2b-4c6d-9e8f-0a1b2c3d4e5f`, UTC+4.
- **23 people**, about half with Arabic display names, preferred language `ar-AE`, `ar-SA`, `ar-EG` or `en-US`, job titles, phones and employee IDs. Two companies (TeamBeats Demo Holding, TeamBeats Demo Services), divisions (Corporate, Shared Services, Field Services) and departments (Human Resources, Finance, Information Technology, Corporate Communications, Legal Affairs, Operations, Executive Office). Edge cases: one disabled leaver (`former.employee`), one new joiner with no organization data (`new.joiner`), one department spelled `HR` instead of `Human Resources` (spec 002 spelling rule).
- **Groups**: TeamBeats Administrators (mapped to tenant administrator), Communications Editors (content editor), IT Support (integration administrator), HR Team, Finance Team, People Managers, All Staff.
- **Administrative units**: Dubai Office, Cairo Office, Riyadh Office.
- **Rooms**: Al Noor Boardroom, Majlis Room, Focus Room 9A, Nile Meeting Room.
- **Mail**: 11 messages in Arabic and English for Layla Haddad, Omar Al Farsi, Sarah Mitchell and Ahmed Mansour.
- **Calendar**: 9 meetings from yesterday to four days ahead, including an all-staff town hall, a budget review in a room and an out of office day. Times move with the day the host starts.

On start, the host connects the seeded tenant through each module's own use cases (so everything is audited as in production): Entra ID single sign-on with the group mappings above, the Microsoft 365 connection with directory sync, mail, calendar and free/busy consented, directory sync settings (deletion threshold 100 percent so testers can disable many fake users), and a first sync, which creates 22 people (the disabled leaver is skipped). Each step is skipped when already done, so restarts are safe.

### Sign in on staging
- **Test admin**: "Sign in as test admin" on the sign-in page (spec 003 US7).
- **Any fictional employee**: "Sign in with Microsoft" opens the fake Microsoft page; pick a person. Layla Haddad is a tenant administrator through her group; Omar Al Farsi is an integration administrator; Maryam Al Abdullah is a content editor; everyone else is an employee.

## 6. Control API for testers (`/dev/fake`)
Development host only, no sign-in needed, fictional data only. Same accepted risk as the staging test admin.

| Call | Effect |
|---|---|
| `GET /dev/fake` | Scenario name, which fakes are down, directories with consent state and counts |
| `POST /dev/fake/graph/{directory}/users` | Add a person (JSON like a scenario user; mail must be on `.example`) |
| `POST /dev/fake/graph/{directory}/users/{id}:disable`, `:enable`, `:delete` | Change a person, then run a sync to see the effect |
| `POST /dev/fake/graph/{directory}/consent:revoke` | Revoke admin consent, to test the needs-consent state |
| `POST /dev/fake/outage/{entra,graph,openrouter}?down=true` | Simulate an outage; `down=false` restores |

The fake state lives in the API host's memory and is rebuilt from the scenario on restart.

## 7. Known limits
- The SSO callback URI is always `https://{host}/...` (spec 003 implementation), so the full browser sign-in through the fake page works on staging and in integration tests but not on the local `http://` Playwright stack. Local Playwright keeps using the development email form; a staging smoke run covers the Microsoft button.
- SharePoint (spec 011 US9) is R2 and has no fake yet; add it with the adapter, as in section 2.
- Content, documents and services have no seed data yet because their modules are not built; add them to the scenario in the same PR as each module.
