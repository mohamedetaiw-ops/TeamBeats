import type { CSSProperties } from "react";

export type PreviewSample = Record<
  "shell" | "navHome" | "navNews" | "heading" | "body" | "secondary" | "link" | "primary" | "secondaryAction" | "success" | "warning" | "danger",
  string
>;

type Pane = { mode: string; dir: "ltr" | "rtl"; lang: string; sample: PreviewSample; label: string };

/**
 * Live preview of the edited brand (spec 033 FR-008): a sample shell, navigation, card, text, link, buttons and states,
 * in light and dark and in both directions. Every pane takes the custom properties the API resolved for its mode, so the
 * preview shows exactly what the theme CSS will serve.
 */
export function BrandPreview({
  preview,
  logoUrl,
  logoAlt,
  panes,
  title,
}: {
  preview: Record<string, Record<string, string>> | null;
  logoUrl: string | null;
  logoAlt: string;
  panes: Pane[];
  title: string;
}) {
  return (
    <section className="brand-section" aria-labelledby="brand-preview-title">
      <h2 id="brand-preview-title">{title}</h2>
      <div className="brand-preview-grid">
        {panes.map((pane) => (
          <figure
            key={`${pane.mode}-${pane.dir}`}
            className="brand-preview"
            dir={pane.dir}
            lang={pane.lang}
            data-mode={pane.mode}
            style={(preview?.[pane.mode] ?? {}) as CSSProperties}
          >
            <figcaption className="brand-preview-caption">{pane.label}</figcaption>
            <div className="brand-preview-shell">
              {/* Brand assets are session-protected API files, so the image optimizer cannot fetch them. */}
              {/* eslint-disable-next-line @next/next/no-img-element */}
              {logoUrl ? <img className="brand-preview-logo" src={logoUrl} alt={logoAlt} /> : null}
              <span>{pane.sample.shell}</span>
            </div>
            <nav className="brand-preview-nav" aria-label={pane.sample.shell}>
              <span aria-current="page">{pane.sample.navHome}</span>
              <span>{pane.sample.navNews}</span>
            </nav>
            <div className="brand-preview-card">
              <h3>{pane.sample.heading}</h3>
              <p>{pane.sample.body}</p>
              <p className="brand-preview-secondary">{pane.sample.secondary}</p>
              <p>
                <a href="#brand-preview-title">{pane.sample.link}</a>
              </p>
              <div className="brand-preview-actions">
                <span className="brand-preview-primary">{pane.sample.primary}</span>
                <span className="brand-preview-outline">{pane.sample.secondaryAction}</span>
              </div>
              <div className="brand-preview-actions">
                <span className="brand-preview-state" data-state="success">
                  {pane.sample.success}
                </span>
                <span className="brand-preview-state" data-state="warning">
                  {pane.sample.warning}
                </span>
                <span className="brand-preview-state" data-state="danger">
                  {pane.sample.danger}
                </span>
              </div>
            </div>
          </figure>
        ))}
      </div>
    </section>
  );
}
