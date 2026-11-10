# Frontend security

The browser is untrusted. The frontend renders what the API allows and never decides access. These rules keep the UI from becoming the weak link.

## Rules

- **Authorization stays on the server.** The UI never checks roles, permissions or packages to decide what someone may do. It may hide a control the API said is not allowed, but every API call is authorized again (constitution III, XXI, XXX).
- **Tenant from the session.** Never put a tenant ID, organization ID or user ID from the URL or a form into an API call as authority. The API derives them from the session.
- **Content Security Policy.** Pages run under the strict CSP in `src/Web/proxy.ts`: scripts only from our origin, no inline scripts, images only from our origin, `data:` and `blob:`, fonts only from our origin. Never add a third-party host; fonts and images are self-hosted. The e2e suite fails on any CSP violation.
- **No raw HTML.** Never use `dangerouslySetInnerHTML` with user or tenant content. Rich text is rendered from the structured Lexical model by the content renderer, which allow-lists nodes and attributes (spec 009, `tests/Architecture/SecureRenderingTests.cs`).
- **No free CSS.** Tenants style only through validated tokens (spec 033 FR-013). Token values are typed on the server; `url()`, expressions and unknown fonts are refused, so a theme cannot load content or run code.
- **Links.** External links open with `rel="noopener noreferrer"` and are marked as external with the `external` icon and text. Never build a URL from untrusted input without validating its scheme (`https`, `mailto`, relative only).
- **Uploads.** Accept only the formats the spec lists (PNG, JPEG, sanitized SVG up to 1 MB for brand assets). The server validates and sanitizes; the browser check is only for convenience.
- **Secrets.** Nothing secret in client code, `NEXT_PUBLIC_*` variables, local storage or logs. Session cookies are HTTP only.
- **Client caches** are keyed by tenant, scope and security context, and cleared on sign out (constitution XXX).
- **Personal data.** Show only what the person may see; mask what the spec says to mask. Do not put personal data in URLs or analytics events.
- **Dependencies.** New packages need a reason the current stack cannot cover, an OSI licence on the CI allow list and a pinned exact version (constitution XXII).
