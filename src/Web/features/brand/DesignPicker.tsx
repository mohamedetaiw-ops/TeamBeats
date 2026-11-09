import type { CSSProperties } from "react";
import type { DesignOption } from "@/lib/brand/types";

/**
 * Design picker (spec 033 FR-031, spec 036): the three TeamBeats designs as cards, each previewed with the custom
 * properties the API resolved for it, so the miniature shows the real colors, type and shape. Choosing one hands the
 * key to the brand editor, which checks and saves it like any other token change.
 */
export function DesignPicker({
  designs,
  current,
  onChoose,
  labels,
}: {
  designs: DesignOption[];
  current: string;
  onChoose: (key: string) => void;
  labels: {
    title: string;
    intro: string;
    replaces: string;
    current: string;
    choose: (name: string) => string;
    name: (key: string) => string;
    summary: (key: string) => string;
    navigation: (style: string) => string;
  };
}) {
  return (
    <section className="brand-section" aria-labelledby="brand-design-title" data-testid="design-picker">
      <h2 id="brand-design-title">{labels.title}</h2>
      <p className="notice">{labels.intro}</p>
      <ul className="design-options">
        {designs.map((d) => {
          const selected = d.key === current;
          return (
            <li key={d.key} className="design-option" data-design-option={d.key} data-selected={selected || undefined}>
              <div className="design-mini" data-nav={d.navigation} style={(d.preview.light ?? {}) as CSSProperties} aria-hidden="true">
                <span className="design-mini-bar" />
                <span className="design-mini-nav" />
                <span className="design-mini-main">
                  <span className="design-mini-title" />
                  <span className="design-mini-card" />
                  <span className="design-mini-card" />
                  <span className="design-mini-button" />
                </span>
              </div>
              <div className="design-option-text">
                <h3>{labels.name(d.key)}</h3>
                <p>{labels.summary(d.key)}</p>
                <p className="notice">{labels.navigation(d.navigation)}</p>
              </div>
              {selected ? (
                <p className="design-option-current">{labels.current}</p>
              ) : (
                <button type="button" className="button secondary" onClick={() => onChoose(d.key)}>
                  {labels.choose(labels.name(d.key))}
                </button>
              )}
            </li>
          );
        })}
      </ul>
      <p className="notice">{labels.replaces}</p>
    </section>
  );
}
