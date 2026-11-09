"use client";

import { useId, useRef, useState, type KeyboardEvent, type ReactNode } from "react";

/**
 * Tabs (WAI-ARIA tabs pattern, automatic activation). Arrow keys follow the reading direction, so the right arrow moves
 * to the previous tab in Arabic. Home and End jump to the first and last tab.
 */
export function Tabs({ label, tabs }: { label: string; tabs: { key: string; label: string; content: ReactNode }[] }) {
  const id = useId();
  const [active, setActive] = useState(0);
  const refs = useRef<(HTMLButtonElement | null)[]>([]);

  function onKey(event: KeyboardEvent<HTMLDivElement>) {
    const rtl = getComputedStyle(event.currentTarget).direction === "rtl";
    const next = { ArrowRight: rtl ? -1 : 1, ArrowLeft: rtl ? 1 : -1 }[event.key];
    let index = active;
    if (next) index = (active + next + tabs.length) % tabs.length;
    else if (event.key === "Home") index = 0;
    else if (event.key === "End") index = tabs.length - 1;
    else return;
    event.preventDefault();
    setActive(index);
    refs.current[index]?.focus();
  }

  return (
    <div className="ui-tabs">
      <div role="tablist" aria-label={label} className="ui-tablist" onKeyDown={onKey}>
        {tabs.map((tab, i) => (
          <button
            key={tab.key}
            ref={(el) => {
              refs.current[i] = el;
            }}
            type="button"
            role="tab"
            id={`${id}-tab-${tab.key}`}
            aria-selected={i === active}
            aria-controls={`${id}-panel-${tab.key}`}
            tabIndex={i === active ? 0 : -1}
            className="ui-tab"
            onClick={() => setActive(i)}
          >
            {tab.label}
          </button>
        ))}
      </div>
      {tabs.map((tab, i) => (
        <div
          key={tab.key}
          role="tabpanel"
          id={`${id}-panel-${tab.key}`}
          aria-labelledby={`${id}-tab-${tab.key}`}
          hidden={i !== active}
          tabIndex={0}
          className="ui-tabpanel"
        >
          {tab.content}
        </div>
      ))}
    </div>
  );
}
