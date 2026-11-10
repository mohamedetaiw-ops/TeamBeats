"use client";

import { useEffect, useId, useRef, type InputHTMLAttributes, type ReactNode } from "react";
import { Icon } from "./Icon";

type Props = Omit<InputHTMLAttributes<HTMLInputElement>, "type" | "children"> & {
  label: ReactNode;
  /** One line under the label that explains the consequence of ticking the box. */
  hint?: string;
  /** Some, but not all, of the items this box stands for are selected. Announced as "mixed". */
  indeterminate?: boolean;
  /** Draws the box, label and hint as one bordered card that is clickable everywhere. */
  card?: boolean;
  error?: string | null;
};

/**
 * Checkbox (spec 036 DLS-034): a native checkbox with our drawing on top, so it keeps the browser's keyboard, form and
 * screen reader behavior. The tick and the mixed dash are drawn with the icon set.
 */
export function Checkbox({ label, hint, indeterminate = false, card = false, error, className, ...input }: Props) {
  const id = useId();
  const ref = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (ref.current) ref.current.indeterminate = indeterminate;
  }, [indeterminate]);
  const describedBy = [hint ? `${id}-hint` : null, error ? `${id}-error` : null].filter(Boolean).join(" ") || undefined;
  return (
    <div className={["ui-choice", className].filter(Boolean).join(" ")} data-card={card || undefined}>
      <span className="ui-choice-control" data-kind="checkbox">
        <input ref={ref} id={id} type="checkbox" aria-describedby={describedBy} aria-invalid={error ? true : undefined} {...input} />
        <span className="ui-choice-mark" aria-hidden="true">
          <Icon name={indeterminate ? "minus" : "check"} size={14} />
        </span>
      </span>
      <span className="ui-choice-text">
        <label htmlFor={id}>{label}</label>
        {hint ? (
          <span id={`${id}-hint`} className="field-hint">
            {hint}
          </span>
        ) : null}
        {error ? (
          <span id={`${id}-error`} className="field-error" role="alert">
            {error}
          </span>
        ) : null}
      </span>
    </div>
  );
}
