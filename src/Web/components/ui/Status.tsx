import type { ReactNode } from "react";

export type StatusTone = "neutral" | "success" | "warning" | "danger" | "info" | "accent";

/**
 * Status dot (spec 036 DLS-038): a live state in few words, such as Online, Syncing or Failed. The dot only repeats
 * what the text says, so the text is required and color never carries the meaning alone.
 */
export function StatusDot({ tone = "neutral", pulse = false, children }: { tone?: StatusTone; pulse?: boolean; children: ReactNode }) {
  return (
    <span className="ui-status-dot" data-tone={tone} data-pulse={pulse || undefined}>
      <span className="ui-status-dot-mark" aria-hidden="true" />
      <span>{children}</span>
    </span>
  );
}

/** A keyboard key or shortcut, such as Ctrl K. Keys are written left to right in every language. */
export function Kbd({ children }: { children: ReactNode }) {
  return (
    <kbd className="ui-kbd" dir="ltr">
      {children}
    </kbd>
  );
}

/**
 * Divider (spec 036 DLS-038). Without a label it is a plain rule between groups; with one, such as "or", the label
 * sits in the rule and is read as the separator's name.
 */
export function Divider({ label, strong = false }: { label?: string; strong?: boolean }) {
  if (!label) {
    return <hr className="ui-divider" data-strong={strong || undefined} />;
  }
  return (
    <div className="ui-divider ui-divider-labelled" role="separator" data-strong={strong || undefined}>
      <span>{label}</span>
    </div>
  );
}

/**
 * Spinner (spec 036 DLS-038): a short wait inside a control or a small area, with words for assistive technology.
 * For a region whose layout is known use Skeleton; for a whole view use StateView.
 */
export function Spinner({ label, size = "md" }: { label: string; size?: "sm" | "md" | "lg" }) {
  return (
    <span className="ui-spinner" data-size={size} role="status">
      <span className="ui-spinner-ring" aria-hidden="true" />
      <span className="visually-hidden">{label}</span>
    </span>
  );
}

/**
 * Progress (spec 036 DLS-038): how far a task has come, such as an upload or a course. Leave `value` out while the
 * amount is unknown. The value text is shown; pass the wording with its unit ("3 of 8 files", "40%").
 */
export function Progress({ label, value, max = 100, valueText }: { label: string; value?: number; max?: number; valueText?: string }) {
  return (
    <div className="ui-progress">
      <div className="ui-progress-head">
        <span className="ui-progress-label">{label}</span>
        {valueText ? <span className="ui-progress-value">{valueText}</span> : null}
      </div>
      <progress aria-label={label} aria-valuetext={valueText} value={value} max={max} />
    </div>
  );
}

/**
 * Meter (spec 036 DLS-038): a measurement inside a known range, such as storage used or a quota. Unlike progress it is
 * not heading for an end. The tone says how the caller judges the level; the text says the level itself.
 */
export function Meter({
  label,
  value,
  min = 0,
  max = 100,
  valueText,
  tone = "accent",
}: {
  label: string;
  value: number;
  min?: number;
  max?: number;
  valueText: string;
  tone?: "accent" | "success" | "warning" | "danger";
}) {
  const share = max > min ? Math.min(1, Math.max(0, (value - min) / (max - min))) : 0;
  return (
    <div className="ui-meter" data-tone={tone}>
      <div className="ui-progress-head">
        <span className="ui-progress-label">{label}</span>
        <span className="ui-progress-value">{valueText}</span>
      </div>
      <div className="ui-meter-track" role="meter" aria-label={label} aria-valuemin={min} aria-valuemax={max} aria-valuenow={value} aria-valuetext={valueText}>
        <span className="ui-meter-fill" style={{ inlineSize: `${share * 100}%` }} />
      </div>
    </div>
  );
}
