# Imagery

Images make the intranet feel like the organization's own place. They must be relevant, respectful, fast and accessible.

## Kinds of imagery

| Kind | Where | Guidance |
|---|---|---|
| Photography | News heroes, event cards, people profiles, campaign banners | Real people and places of the organization. Natural light, authentic moments, diverse and representative, culturally appropriate for the region. No staged stock clichés |
| Illustration | Empty states, onboarding, sign-in, sample content | Flat shapes in the design's palette, as in the gallery samples (`public/design-system`). Simple enough to read at small sizes |
| Brand assets | Logo, lockup, favicon, default and sign-in images | Uploaded per tenant in Administration, Brand; PNG, JPEG or sanitized SVG up to 1 MB (spec 033) |
| Avatars | People everywhere | Photo when the person provided one, otherwise initials on the accent tint (`<Avatar>`) |

## Rules

- **Always a fixed aspect ratio.** Use `<Media ratio="16:9|4:3|1:1|3:1">`, so space is reserved before the image loads and nothing shifts (Cumulative Layout Shift under 0.1).
- **Alternative text is required.** Describe what the image shows and why it matters in the context, in the page language, in about one sentence. If the surrounding text already says everything, mark the image `decorative`. Never write "image of". The build fails on `<img>` without `alt`.
- **No text in images.** Text in a picture cannot be translated, resized or read by screen readers. Put it in the page.
- **Faces and right to left.** Do not mirror photos of people or places for Arabic; mirror only abstract directional illustrations.
- **Respect.** Get consent for photos of employees; follow the tenant's guidance on modesty and representation; avoid gestures and symbols that read differently across cultures.
- **Weight.** Upload the largest needed size only (hero 1600 px wide, cards 800 px). Images below the first screen load lazily (default in `<Media>`); the hero uses `priority`.
- **Dark mode.** Illustrations sit on `surface.sunken` so they keep contrast; photos need no change.
