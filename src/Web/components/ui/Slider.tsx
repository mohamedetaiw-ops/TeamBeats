"use client";

import { useId, useState, type CSSProperties, type InputHTMLAttributes } from "react";

/**
 * Slider (spec 036 DLS-034): an approximate number in a range, such as a text size or a radius. The native range input
 * gives arrow keys, Home, End and Page keys; the current value is always shown as text beside the label. For an exact
 * number, use a number field.
 */
export function Slider({
  label,
  min = 0,
  max = 100,
  step = 1,
  value,
  defaultValue,
  onValueChange,
  format = (v) => String(v),
  hint,
  ...input
}: Omit<InputHTMLAttributes<HTMLInputElement>, "type" | "min" | "max" | "step" | "value" | "defaultValue" | "children"> & {
  label: string;
  min?: number;
  max?: number;
  step?: number;
  value?: number;
  defaultValue?: number;
  onValueChange?: (value: number) => void;
  /** Turns the number into the text people read and hear, with its unit. */
  format?: (value: number) => string;
  hint?: string;
}) {
  const id = useId();
  const [inner, setInner] = useState(defaultValue ?? min);
  const current = value ?? inner;
  const fill = max > min ? ((current - min) / (max - min)) * 100 : 0;
  return (
    <div className="ui-slider">
      <div className="ui-slider-head">
        <label htmlFor={id}>{label}</label>
        <output htmlFor={id}>{format(current)}</output>
      </div>
      <input
        id={id}
        type="range"
        min={min}
        max={max}
        step={step}
        value={current}
        aria-valuetext={format(current)}
        aria-describedby={hint ? `${id}-hint` : undefined}
        style={{ "--xp-fill": `${fill}%` } as CSSProperties}
        onChange={(e) => {
          const next = Number(e.currentTarget.value);
          setInner(next);
          onValueChange?.(next);
        }}
        {...input}
      />
      {hint ? (
        <p id={`${id}-hint`} className="field-hint">
          {hint}
        </p>
      ) : null}
    </div>
  );
}
