"use client";

import { useId, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { Icon, type IconName } from "./Icon";

/**
 * Tabs (WAI-ARIA tabs pattern, automatic activation). Arrow keys follow the reading direction, so the right arrow moves
 * to the previous tab in Arabic. Home and End jump to the first and last tab. Vertical tabs, such as the sections of a
 * settings dialog, sit at the start and move with the up and down arrows.
 */
export function Tabs({
  label,
  tabs,
  orientation = "horizontal",
  initialKey,
}: {
  label: string;
  tabs: { key: string; label: string; icon?: IconName; content: ReactNode }[];
  orientation?: "horizontal" | "vertical";
  /** The tab to open first, such as one named in a link; the first tab when it is missing or unknown. */
  initialKey?: string;
}) {
  const id = useId();
  const [active, setActive] = useState(() => Math.max(0, tabs.findIndex((tab) => tab.key === initialKey)));
  const refs = useRef<(HTMLButtonElement | null)[]>([]);

  function onKey(event: KeyboardEvent<HTMLDivElement>) {
    const rtl = getComputedStyle(event.currentTarget).direction === "rtl";
    const keys: Record<string, number> =
      orientation === "vertical" ? { ArrowDown: 1, ArrowUp: -1 } : { ArrowRight: rtl ? -1 : 1, ArrowLeft: rtl ? 1 : -1 };
    const next = keys[event.key];
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
    <div className="ui-tabs" data-orientation={orientation === "vertical" ? orientation : undefined}>
      <div role="tablist" aria-label={label} aria-orientation={orientation === "vertical" ? orientation : undefined} className="ui-tablist" onKeyDown={onKey}>
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
            {tab.icon ? <Icon name={tab.icon} size={18} /> : null}
            <span>{tab.label}</span>
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
