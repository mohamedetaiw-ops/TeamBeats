import { useLocale } from "next-intl";
import { dayInWords } from "@/lib/format";
import { Icon, type IconName } from "@/components/ui/Icon";
import { useId, type FieldsetHTMLAttributes, type FormHTMLAttributes, type HTMLAttributes, type InputHTMLAttributes, type ReactNode, type Ref, type SelectHTMLAttributes, type TextareaHTMLAttributes } from "react";

type Props = InputHTMLAttributes<HTMLInputElement> & {
  label: string;
  name: string;
  error?: string | null;
  hint?: string;
  /** A part before the text, in reading order: an icon, a currency, a fixed prefix (spec 036 DLS-035). */
  leading?: ReactNode;
  /** A part after the text: a unit, a domain, a small action. */
  trailing?: ReactNode;
  /** The input itself, for a form that moves focus to it. */
  ref?: Ref<HTMLInputElement>;
};

/**
 * Labelled input with an optional hint under the control, so a row of controls lines up (FR-046, DLS-049) and an associated error (WCAG 2.2 AA: 1.3.1, 3.3.1; FR-038). The error
 * is linked to the field and announced, and the input keeps what the user typed.
 */
export function FormField({ label, error, hint, leading, trailing, ...input }: Props) {
  const id = useId();
  const locale = useLocale();
  const errorId = `${id}-error`;
  const hintId = `${id}-hint`;
  const dayId = `${id}-day`;
  // A date field writes the chosen day in words under the control (DLS-082).
  const day = input.type === "date" && typeof input.value === "string" ? dayInWords(input.value, locale) : null;
  const describedBy = [day ? dayId : null, hint ? hintId : null, error ? errorId : null].filter(Boolean).join(" ") || undefined;
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
      {day ? <DayReadout id={dayId} text={day} /> : null}
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

/** The chosen day of a date field in words, under the control (DLS-082). */
export function DayReadout({ id, text }: { id?: string; text: string }) {
  return (
    <p id={id} className="field-hint field-day">
      {text}
    </p>
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

type FormPanelProps =
  | ({ as: "form" } & Omit<FormHTMLAttributes<HTMLFormElement>, "className">)
  | ({ as: "fieldset" } & Omit<FieldsetHTMLAttributes<HTMLFieldSetElement>, "className">)
  | ({ as?: "section" } & Omit<HTMLAttributes<HTMLElement>, "className">);

/**
 * Form panel (docs/design-system/components.md, Form panel; spec 036 T037a): the one raised surface a form, a group of
 * fields or a set of record actions sits on, so a check-in form, an audience fieldset and an import form look the same
 * in every design. A fieldset keeps its legend as the group name; a form or section names itself with a heading.
 */
export function FormPanel(props: FormPanelProps) {
  if (props.as === "form") {
    const { as: _form, ...rest } = props;
    return <form className="ui-form-panel" {...rest} />;
  }
  if (props.as === "fieldset") {
    const { as: _fieldset, ...rest } = props;
    return <fieldset className="ui-form-panel" {...rest} />;
  }
  const { as: _section, ...rest } = props;
  return <section className="ui-form-panel" {...rest} />;
}

/**
 * One section of a settings page (spec 036 DLS-047): an icon, a heading and one line of help on the start side, the
 * controls on the end side; one column below 40rem.
 */
export function SettingsSection({ id, icon, title, hint, children }: { id: string; icon: IconName; title: string; hint: string; children: ReactNode }) {
  return (
    <section className="ui-settings-section" aria-labelledby={id}>
      <div className="ui-settings-section-head">
        <span className="ui-icon-tile" aria-hidden="true">
          <Icon name={icon} size={18} />
        </span>
        <div>
          <h2 id={id}>{title}</h2>
          <p>{hint}</p>
        </div>
      </div>
      <div className="ui-settings-section-body">{children}</div>
    </section>
  );
}
