"use client";

import { Children, useId, useRef, useState, type KeyboardEvent, type ReactNode } from "react";

/**
 * Tabbed regions for the panel layout (L09). WAI-ARIA tabs: arrow keys move between tabs, Home and End jump, and
 * arrows follow the reading direction, so right-to-left pages move the natural way.
 */
export function RegionTabs({ label, tabs, children }: { label: string; tabs: { id: string; label: string }[]; children: ReactNode }) {
  const base = useId();
  const [active, setActive] = useState(0);
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  const panels = Children.toArray(children);

  function onKey(e: KeyboardEvent<HTMLDivElement>) {
    const rtl = getComputedStyle(e.currentTarget).direction === "rtl";
    const next = { ArrowRight: rtl ? -1 : 1, ArrowLeft: rtl ? 1 : -1 }[e.key];
    let index = active;
    if (next !== undefined) index = (active + next + tabs.length) % tabs.length;
    else if (e.key === "Home") index = 0;
    else if (e.key === "End") index = tabs.length - 1;
    else return;
    e.preventDefault();
    setActive(index);
    refs.current[index]?.focus();
  }

  return (
    <>
      <div role="tablist" aria-label={label} className="xp-tabs" onKeyDown={onKey}>
        {tabs.map((tab, i) => (
          <button key={tab.id} ref={(el) => { refs.current[i] = el; }} type="button" role="tab" id={`${base}-tab-${i}`}
            aria-selected={i === active} aria-controls={`${base}-panel-${i}`} tabIndex={i === active ? 0 : -1}
            className="xp-tab" onClick={() => setActive(i)}>
            {tab.label}
          </button>
        ))}
      </div>
      {panels.map((panel, i) => (
        <div key={tabs[i]?.id ?? i} role="tabpanel" id={`${base}-panel-${i}`} aria-labelledby={`${base}-tab-${i}`} hidden={i !== active}
          className="xp-row-grid">
          {panel}
        </div>
      ))}
    </>
  );
}
