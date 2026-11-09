# ADR-0019: Self-hosted fonts for the default TeamBeats brand

| Field | Value |
|---|---|
| Status | Accepted (2026-11-09, under Mohamed's standing approval to proceed through R1) |
| Date | 2026-11-09 |
| Owners | Mohamed |
| Related specs | 033 (FR-006, FR-007, US3 scenario 2, NFR on font loading), 008, 007; constitution VI (Arabic first), XXX; ADR-0014 |

## Context and constraints
The v2 visual direction Mohamed reviewed uses Readex Pro for text and Alexandria for headings. Both cover Arabic and Latin from one family, so a bilingual page keeps one voice. Until now the web app loaded no font files: the token defaults named Inter and Noto Sans Arabic and relied on whatever the device had installed. Spec 033 asks for fonts from an approved list, a fallback with no missing glyphs, and font files subset per script and loaded only for the locales in use. Pages must not call third-party font hosts, because tenant pages may carry Confidential data and some installs block egress.

## Options considered
1. **Keep system fonts.** No download cost, but the look differs per device and the Arabic face is often poor. Rejected.
2. **Google Fonts from its CDN.** Easy, but every page view would tell a third party which tenant is reading. Rejected.
3. **Self-host the font files from the `@fontsource` packages (chosen).** Each weight ships as per-script files (Arabic, Latin, Latin extended) with `unicode-range`, so a browser downloads only the scripts a page uses. Next.js bundles them under the app's own origin.

## Decision
Option 3.
- Add `@fontsource/readex-pro` and `@fontsource/alexandria`, pinned to exact versions. Load Readex Pro at 400, 500 and 700 and Alexandria at 600 and 700 in the root layout.
- Add Readex Pro and Alexandria to the approved Latin and Arabic font lists. Make them the platform defaults for body text (`font.family.latin`, `font.family.arabic`). Add two heading tokens (`font.family.heading_latin`, `font.family.heading_arabic`) that feed `--tb-font-family-heading`. Urdu and Hindi headings use the body font of their script.
- Both families are under the SIL Open Font License 1.1, which is OSI-approved. The CI licence check allows `OFL-1.1` for font packages.

## Consequences
- Pages look the same on every device in both directions. Each locale costs about 30 to 60 KB of font files per weight on first load; the files are then cached.
- Tenants keep choosing their own fonts from the approved list; brands that name another family are unchanged.
- A new weight or family is a change to this list and the approved fonts, not a per-customer setting.
