# Demo data package

The demo data package fills a tenant with realistic, fictional Arabic and English content so that every in-app area of the intranet has something to show in a product demo or a test session. It sits beside the fake connections ([fake-systems.md](fake-systems.md)): the fakes give the connector data (directory, Outlook mail and calendar, rooms), and this package gives the data that lives in TeamBeats itself.

It adds no product behavior: every item goes in through the existing use cases of spec 002 (org units), spec 004 (roles), spec 009 (news and knowledge), spec 010 (profiles), spec 011 (documents), spec 012 (search tuning), spec 014 (assistant), spec 016 (services and requests), spec 022 (working calendars), spec 025 (policies and attestations) and spec 029 (spaces).

## Where it lives

- Files: `src/Api.DevHost/seeds/demo/*.json`. The host reads every file in the folder in name order and merges them, so a new area can be added as a new file.
- Code: `src/Api.DevHost/Demo/`. `DemoDataSeeder` runs on start in Development and Staging when `Dev:SeedDemo:Folder` is set. The production host never references it.
- Test: `tests/Integration/DemoDataSeedTests.cs`.

## What it adds

| File | Area | Content |
|---|---|---|
| `01-people.json` | People and access | 21 personas from the fake directory, Arabic and English names, time zones, a `policy_owner` role, role assignments (publishers, approvers, compliance officer), Arabic names for org units |
| `02-news.json` | News | 11 stories and a pinned announcement, with reactions and comments from colleagues |
| `03-knowledge.json` | Knowledge | 6 how-to articles, each with an owner |
| `04-services.json` | Services | 6 categories, 24 services (forms and linked apps) with approval routes, favourites, 30 requests in every state with approval decisions |
| `05-documents.json` | Documents | 4 libraries with folders and 13 files (English PDFs, Arabic Markdown) |
| `06-policies.json` | Policies | 4 categories, 6 approved policies with clauses, one attestation campaign |
| `07-calendars-search-assistant.json` | Calendars, search, assistant | UAE, Saudi and Egypt working calendars with public holidays for 2026 and 2027, best bets, synonym sets, assistant conversations |
| `08-calendar-events.json` | Calendar events (spec 037) | A town hall with RSVP, a weekly wellness series, a fire drill, a three day book fair and a family day, published by the administrator to the whole company |
| `09-spaces.json` | Spaces (spec 029) | A private finance rollout project with members, two milestones, five tasks and an at risk status, and a public People team space with three tasks |

Every name, email address and number is invented. People are addressed by persona key (for example `layla`); `admin` is always the first configured test administrator. In staging the persona emails come from the `staging-demo` scenario; set `Dev:SeedDemo:People:<persona>` to point a persona at another account.

## How it runs

- Each item is created through its owner module's use cases, as the person the file names (the author writes the story, the approver decides the task, the employee submits the request), so audit, events, search indexing and permissions all behave as in real use. If that person cannot do it, the administrator does it instead.
- Each step runs in its own scope and transaction with the tenant set, so forced row level security applies.
- In the hosts, `DemoClock` backdates items by the `daysAgo` in the file, so the feed and the inbox look lived in. Tests use the real clock.
- It waits until the administrator has signed in once, like the home seed.
- Items are found again by English title or name (requests by idempotency key), so a restart adds nothing. A person who does not exist is reported as `<persona> not found` and the rest carries on. Documents whose stored file was lost (staging keeps files in temporary storage) are uploaded again.
- The log line `Demo data for <tenant>: added ...; skipped ...` lists what the run did.

## Changing it

Edit the JSON and restart the host. Keep content fictional, keep both languages for anything a person reads, and make synonyms single words. Run `DemoDataSeedTests` after a change: it checks that every item goes in, that a second run adds nothing and that another tenant sees none of it.

## Known limits

- The organization is built from the synced directory; a tenant with no directory sync (as in the integration test) gets no org units.
- Mockup cards whose components are not built yet (AI briefing, recognition, celebrations, polls, communities, leave balance, analytics) get their data when those modules are built.
