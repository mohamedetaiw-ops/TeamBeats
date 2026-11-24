# Quarterly design audit

The gates in `npm run verify` and the design suite stop new drift on every pull request. They cannot see everything:
wording, a pattern used in the wrong place, a tenant brand that drifted after publishing. Once a quarter the design
owner runs this audit (spec 036 R2 T013) and keeps the result in `docs/design-system/audits/<year>-Q<n>.md`.

## 1. The counted part

On the first day of each quarter the `design-audit` workflow runs `npm run report:design` in `src/Web` and attaches the
report to the run. It can also be run by hand at any time:

```
cd src/Web
node scripts/design-audit.mjs ../../docs/design-system/audits/2026-Q4.md
```

It counts, from the code only:

- pages still without the shared page header (the baseline of the design gate, DLS-105);
- own tab strips and filter bars still waiting to move onto the shared parts (DLS-106);
- home placements off the Widget frame (DLS-089);
- feature classes still on the Studio surface list, each a shared part to build (T037a);
- the shared components and how many the gallery shows (DLS-117), and the gallery sections;
- accessibility exclusions with their review dates (spec 038 A11Y-008), with overdue ones marked.

Each gate fails a new entry, so these numbers only go down. A number that went up means a gate was loosened; the audit
names the change that did it.

## 2. The live screen pass

The reviewer walks the primary pages of spec 038 in each of the four designs, light and dark, in Arabic and English,
at desktop and phone width, and compares them with the gallery and the [detail checklist](foundations/details.md).
Each finding names the page, the rule it breaks (a DLS number) and the fix order.

## 3. The drift report

For each pilot tenant the reviewer opens the drift report in the brand manager (spec 033 T022) and records the counts
of non-compliant asset usage, conflicting overrides and inaccessible elements, with the remediation agreed with the
tenant's brand owner. The audit file holds counts only: no tenant names, people or content.

## 4. Actions

The audit ends with what it changes: baselines to shrink, components to add to the gallery, tasks opened in the spec
that owns the screen. The next audit starts by checking those actions.
