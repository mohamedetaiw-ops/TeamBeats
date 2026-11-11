import { useId, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from "react";

type Props = InputHTMLAttributes<HTMLInputElement> & {
  label: string;
  name: string;
  error?: string | null;
  hint?: string;
  /** A part before the text, in reading order: an icon, a currency, a fixed prefix (spec 036 DLS-035). */
  leading?: ReactNode;
  /** A part after the text: a unit, a domain, a small action. */
  trailing?: ReactNode;
};

/**
 * Labelled input with an optional hint under the control, so a row of controls lines up (FR-046, DLS-049) and an associated error (WCAG 2.2 AA: 1.3.1, 3.3.1; FR-038). The error
 * is linked to the field and announced, and the input keeps what the user typed.
 */
export function FormField({ label, error, hint, leading, trailing, ...input }: Props) {
  const id = useId();
  const errorId = `${id}-error`;
  const hintId = `${id}-hint`;
  const describedBy = [hint ? hintId : null, error ? errorId : null].filter(Boolean).join(" ") || undefined;
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      {leading || trailing ? (
        <InputGroup leading={leading} trailing={trailing}>
          <input id={id} aria-invalid={error ? true : undefined} aria-describedby={describedBy} {...input} />
        </InputGroup>
      ) : (
        <input id={id} aria-invalid={error ? true : undefined} aria-describedby={describedBy} {...input} />
      )}
      {hint ? (
        <p id={hintId} className="field-hint">
          {hint}
        </p>
      ) : null}
      {error ? (
        <p id={errorId} className="field-error" role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
}

/**
 * One box around a text input and the parts beside it, so the border, focus ring and error state belong to the whole.
 * The parts are decoration or small controls; the label still names the input.
 */
export function InputGroup({ leading, trailing, children }: { leading?: ReactNode; trailing?: ReactNode; children: ReactNode }) {
  return (
    <span className="ui-input-group">
      {leading ? <span className="ui-input-part">{leading}</span> : null}
      {children}
      {trailing ? <span className="ui-input-part">{trailing}</span> : null}
    </span>
  );
}

/** Shared label, hint and error wiring for the text box variants below and the other fields of the catalogue. */
export function Field({
  label,
  hint,
  error,
  children,
}: {
  label: string;
  hint?: string;
  error?: string | null;
  children: (control: { id: string; describedBy?: string; invalid?: true }) => ReactNode;
}) {
  const id = useId();
  const describedBy = [hint ? `${id}-hint` : null, error ? `${id}-error` : null].filter(Boolean).join(" ") || undefined;
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      {children({ id, describedBy, invalid: error ? true : undefined })}
      {hint ? (
        <p id={`${id}-hint`} className="field-hint">
          {hint}
        </p>
      ) : null}
      {error ? (
        <p id={`${id}-error`} className="field-error" role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
}

/** Multi-line text box. The visible counter appears only when a maximum length is set. */
export function TextAreaField({
  label,
  hint,
  error,
  counter,
  ...textarea
}: TextareaHTMLAttributes<HTMLTextAreaElement> & { label: string; name: string; error?: string | null; hint?: string; counter?: string }) {
  return (
    <Field label={label} hint={hint} error={error}>
      {({ id, describedBy, invalid }) => (
        <>
          <textarea id={id} aria-invalid={invalid} aria-describedby={describedBy} rows={4} {...textarea} />
          {counter ? (
            <p className="field-hint" aria-live="polite">
              {counter}
            </p>
          ) : null}
        </>
      )}
    </Field>
  );
}

/** Select list for five to fifteen known options; fewer than five read better as radio buttons. */
export function SelectField({
  label,
  hint,
  error,
  options,
  ...select
}: SelectHTMLAttributes<HTMLSelectElement> & {
  label: string;
  name: string;
  error?: string | null;
  hint?: string;
  options: { value: string; label: string }[];
}) {
  return (
    <Field label={label} hint={hint} error={error}>
      {({ id, describedBy, invalid }) => (
        <select id={id} aria-invalid={invalid} aria-describedby={describedBy} {...select}>
          {options.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      )}
    </Field>
  );
}
