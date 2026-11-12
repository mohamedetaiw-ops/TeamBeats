# Imagery

Images make the intranet feel like the organization's own place. They must be relevant, respectful, fast and accessible.

## Kinds of imagery

| Kind | Where | Guidance |
|---|---|---|
| Photography | News heroes, event cards, people profiles, campaign banners | Real people and places of the organization. Natural light, authentic moments, diverse and representative, culturally appropriate for the region. No staged stock clichés |
| Illustration | Empty states, onboarding, sign-in, sample content | Flat shapes in the design's palette, as in the gallery samples (`public/design-system`). Simple enough to read at small sizes |
| Brand assets | Logo, lockup, favicon, default and sign-in images | Uploaded per tenant in Administration, Brand; PNG, JPEG or sanitized SVG up to 1 MB (spec 033) |
| Product marks | Connections area and its "Add connection" wizard | The published mark of a product TeamBeats connects to, unaltered, on a plain plate beside its name (spec 036 DLS-076). Files and sources are listed below |
| Avatars | People everywhere | Photo when the person provided one, otherwise initials on the accent tint (`<Avatar>`) |

## Rules

- **Always a fixed aspect ratio.** Use `<Media ratio="16:9|4:3|1:1|3:1">`, so space is reserved before the image loads and nothing shifts (Cumulative Layout Shift under 0.1).
- **Alternative text is required.** Describe what the image shows and why it matters in the context, in the page language, in about one sentence. If the surrounding text already says everything, mark the image `decorative`. Never write "image of". The build fails on `<img>` without `alt`.
- **No text in images.** Text in a picture cannot be translated, resized or read by screen readers. Put it in the page.
- **Faces and right to left.** Do not mirror photos of people or places for Arabic; mirror only abstract directional illustrations.
- **Respect.** Get consent for photos of employees; follow the tenant's guidance on modesty and representation; avoid gestures and symbols that read differently across cultures.
- **Weight.** Upload the largest needed size only (hero 1600 px wide, cards 800 px). Images below the first screen load lazily (default in `<Media>`); the hero uses `priority`.
- **Dark mode.** Illustrations sit on `surface.sunken` so they keep contrast; photos need no change.

## Product marks

Marks of products TeamBeats connects to live in `src/Web/public/connectors`, and `src/Web/lib/integrations/catalogue.ts` says which type uses which. They name the product only; they are never TeamBeats branding, never recoloured or reshaped, and always sit next to the product's written name, so the image is decorative.

| File | Used for | Source |
|---|---|---|
| `microsoft.svg` | Microsoft 365, on-premises Active Directory | Microsoft logo: four squares in the published colours |
| `google.svg` | Google Workspace | Google "G" mark, Google brand resource center |
| `slack.svg` | Slack | Slack mark in its four colours, Slack brand guidelines (path from Simple Icons, CC0) |
| `atlassian.svg` | Jira Service Management | Atlassian mark in Atlassian blue, Atlassian logo library (path from Simple Icons, CC0) |

ServiceNow, custom REST, webhooks, SFTP and file imports, and generic HR and finance systems use an icon from the icon set. If an owner asks, delete the file and its entry in `catalogue.ts`; the page falls back to the icon.
