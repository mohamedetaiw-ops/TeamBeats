import { useId, type ChangeEventHandler } from "react";

export type RadioOption = { value: string; label: string; hint?: string; disabled?: boolean };

/**
 * Radio group (spec 036 DLS-034): one choice out of two to five, all visible. A fieldset with a legend names the
 * question; native radios give arrow key movement that follows the reading direction. More than five options read
 * better as a select.
 */
export function RadioGroup({
  legend,
  name,
  options,
  value,
  defaultValue,
  onChange,
  hint,
  error,
  cards = false,
  required,
}: {
  legend: string;
  name: string;
  options: readonly RadioOption[];
  value?: string;
  defaultValue?: string;
  onChange?: ChangeEventHandler<HTMLInputElement>;
  hint?: string;
  error?: string | null;
  /** Draws each option as a bordered card with its hint, for choices that need a line of explanation. */
  cards?: boolean;
  required?: boolean;
}) {
  const id = useId();
  const describedBy = [hint ? `${id}-hint` : null, error ? `${id}-error` : null].filter(Boolean).join(" ") || undefined;
  return (
    <fieldset className="ui-choice-group" data-cards={cards || undefined} aria-describedby={describedBy} aria-invalid={error ? true : undefined}>
      <legend>{legend}</legend>
      {hint ? (
        <p id={`${id}-hint`} className="field-hint">
          {hint}
        </p>
      ) : null}
      <div className="ui-choice-list">
        {options.map((option) => {
          const optionId = `${id}-${option.value}`;
          return (
            <div key={option.value} className="ui-choice" data-card={cards || undefined}>
              <span className="ui-choice-control" data-kind="radio">
                <input
                  id={optionId}
                  type="radio"
                  name={name}
                  value={option.value}
                  checked={value === undefined ? undefined : value === option.value}
                  defaultChecked={value === undefined && defaultValue !== undefined ? defaultValue === option.value : undefined}
                  onChange={onChange}
                  disabled={option.disabled}
                  required={required}
                  aria-describedby={option.hint ? `${optionId}-hint` : undefined}
                />
                <span className="ui-choice-mark" aria-hidden="true" />
              </span>
              <span className="ui-choice-text">
                <label htmlFor={optionId}>{option.label}</label>
                {option.hint ? (
                  <span id={`${optionId}-hint`} className="field-hint">
                    {option.hint}
                  </span>
                ) : null}
              </span>
            </div>
          );
        })}
      </div>
      {error ? (
        <p id={`${id}-error`} className="field-error" role="alert">
          {error}
        </p>
      ) : null}
    </fieldset>
  );
}
