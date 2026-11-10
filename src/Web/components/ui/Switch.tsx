import { useId, type InputHTMLAttributes, type ReactNode } from "react";

/**
 * Switch (spec 036 DLS-034): a setting that takes effect at once, such as "Email me a daily summary". It is a native
 * checkbox with the switch role, so Space toggles it and forms submit it. Inside a form that is saved with a button,
 * use a checkbox instead: a switch promises an immediate effect.
 */
export function Switch({
  label,
  hint,
  card = false,
  className,
  ...input
}: Omit<InputHTMLAttributes<HTMLInputElement>, "type" | "role" | "children"> & { label: ReactNode; hint?: string; card?: boolean }) {
  const id = useId();
  return (
    <div className={["ui-choice", "ui-switch", className].filter(Boolean).join(" ")} data-card={card || undefined}>
      <span className="ui-choice-text">
        <label htmlFor={id}>{label}</label>
        {hint ? (
          <span id={`${id}-hint`} className="field-hint">
            {hint}
          </span>
        ) : null}
      </span>
      <span className="ui-switch-control">
        <input id={id} type="checkbox" role="switch" aria-describedby={hint ? `${id}-hint` : undefined} {...input} />
        <span className="ui-switch-track" aria-hidden="true">
          <span className="ui-switch-thumb" />
        </span>
      </span>
    </div>
  );
}
