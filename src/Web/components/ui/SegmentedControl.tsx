"use client";

import { useState, type CSSProperties } from "react";

/**
 * Segmented control (spec 036 DLS-034): one view setting out of two to five short options, such as Day, Week, Month.
 * It is a radio group, so arrow keys move the choice in the reading direction and it submits with a form. A thumb
 * slides under the chosen segment; the segments share the width equally so the thumb needs no measuring.
 */
export function SegmentedControl({
  label,
  name,
  options,
  value,
  defaultValue,
  onChange,
}: {
  label: string;
  name: string;
  options: readonly { value: string; label: string }[];
  value?: string;
  defaultValue?: string;
  onChange?: (value: string) => void;
}) {
  const [inner, setInner] = useState(defaultValue ?? options[0]?.value ?? "");
  const current = value ?? inner;
  const index = Math.max(
    0,
    options.findIndex((o) => o.value === current),
  );
  return (
    <div role="radiogroup" aria-label={label} className="ui-segmented" style={{ "--xp-count": options.length, "--xp-index": index } as CSSProperties}>
      <span className="ui-segmented-thumb" aria-hidden="true" />
      {options.map((option) => (
        <label key={option.value} className="ui-segment">
          <input
            type="radio"
            name={name}
            value={option.value}
            checked={option.value === current}
            onChange={() => {
              setInner(option.value);
              onChange?.(option.value);
            }}
          />
          <span>{option.label}</span>
        </label>
      ))}
    </div>
  );
}
