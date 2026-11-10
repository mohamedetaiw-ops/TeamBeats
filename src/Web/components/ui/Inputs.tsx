"use client";

import { useId, useRef, useState, type ClipboardEvent, type InputHTMLAttributes, type KeyboardEvent } from "react";
import { useTranslations } from "next-intl";
import { Field, InputGroup } from "./FormField";
import { Icon } from "./Icon";

type FieldText = { label: string; hint?: string; error?: string | null };

/**
 * Search field (spec 036 DLS-035): a search box with a magnifier and a clear button that appears once there is text.
 * Escape clears it too. `onSearch` runs on Enter; filtering as the person types is the caller's choice through
 * `onValueChange`.
 */
export function SearchField({
  label,
  hint,
  value,
  onValueChange,
  onSearch,
  ...input
}: Omit<InputHTMLAttributes<HTMLInputElement>, "type" | "value" | "onChange"> &
  Omit<FieldText, "error"> & { value: string; onValueChange: (value: string) => void; onSearch?: (value: string) => void }) {
  const t = useTranslations("ui.controls");
  const ref = useRef<HTMLInputElement>(null);
  return (
    <Field label={label} hint={hint}>
      {({ id, describedBy }) => (
        <InputGroup
          leading={<Icon name="search" size={18} />}
          trailing={
            value ? (
              <button
                type="button"
                className="ui-input-action"
                aria-label={t("clear")}
                onClick={() => {
                  onValueChange("");
                  ref.current?.focus();
                }}
              >
                <Icon name="close" size={16} />
              </button>
            ) : null
          }
        >
          <input
            ref={ref}
            id={id}
            type="search"
            aria-describedby={describedBy}
            value={value}
            onChange={(event) => onValueChange(event.currentTarget.value)}
            onKeyDown={(event) => {
              if (event.key === "Escape" && value) {
                event.preventDefault();
                onValueChange("");
              } else if (event.key === "Enter" && onSearch) {
                event.preventDefault();
                onSearch(value);
              }
            }}
            {...input}
          />
        </InputGroup>
      )}
    </Field>
  );
}

/**
 * Number field (spec 036 DLS-035): a number with minus and plus buttons. It follows the spin button pattern: the arrow
 * keys step, Page Up and Page Down step ten times, Home and End go to the limits when there are limits. Typing is
 * free; the value is brought inside the limits when the field is left. The buttons are for the pointer and stay out
 * of the tab order, because the keys already do their work.
 */
export function NumberField({
  label,
  hint,
  error,
  name,
  value,
  onValueChange,
  min,
  max,
  step = 1,
  unit,
  disabled,
}: FieldText & {
  name: string;
  value: number | null;
  onValueChange: (value: number | null) => void;
  min?: number;
  max?: number;
  step?: number;
  /** Shown after the number, such as "days". */
  unit?: string;
  disabled?: boolean;
}) {
  const t = useTranslations("ui.controls");
  const [draft, setDraft] = useState<string | null>(null);
  const text = draft ?? (value === null ? "" : String(value));
  const clamp = (n: number) => Math.min(max ?? Infinity, Math.max(min ?? -Infinity, n));
  // Steps of 0.1 would otherwise show binary noise such as 0.30000000000000004.
  const tidy = (n: number) => Number(n.toFixed(10));
  const stepBy = (count: number) => {
    setDraft(null);
    onValueChange(tidy(clamp((value ?? min ?? 0) + count * step)));
  };

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    const moves: Record<string, () => void> = {
      ArrowUp: () => stepBy(1),
      ArrowDown: () => stepBy(-1),
      PageUp: () => stepBy(10),
      PageDown: () => stepBy(-10),
    };
    if (event.key === "Home" && min !== undefined) moves.Home = () => onValueChange(min);
    if (event.key === "End" && max !== undefined) moves.End = () => onValueChange(max);
    const move = moves[event.key];
    if (!move) return;
    event.preventDefault();
    setDraft(null);
    move();
  }

  const stepper = (direction: 1 | -1) => (
    <button
      type="button"
      className="ui-input-action"
      tabIndex={-1}
      aria-label={direction === 1 ? t("increase") : t("decrease")}
      disabled={disabled || (direction === 1 ? max !== undefined && (value ?? -Infinity) >= max : min !== undefined && (value ?? Infinity) <= min)}
      onClick={() => stepBy(direction)}
    >
      <Icon name={direction === 1 ? "plus" : "minus"} size={16} />
    </button>
  );

  return (
    <Field label={label} hint={hint} error={error}>
      {({ id, describedBy, invalid }) => (
        <span className="ui-number">
          <InputGroup
            leading={stepper(-1)}
            trailing={
              <>
                {unit ? <span className="ui-input-unit">{unit}</span> : null}
                {stepper(1)}
              </>
            }
          >
            <input
              id={id}
              name={name}
              type="text"
              inputMode="decimal"
              role="spinbutton"
              autoComplete="off"
              aria-valuenow={value ?? undefined}
              aria-valuemin={min}
              aria-valuemax={max}
              aria-valuetext={value !== null && unit ? `${value} ${unit}` : undefined}
              aria-invalid={invalid}
              aria-describedby={describedBy}
              disabled={disabled}
              value={text}
              onChange={(event) => {
                const next = event.currentTarget.value;
                setDraft(next);
                const parsed = Number(next.trim().replace(",", "."));
                if (next.trim() === "") onValueChange(null);
                else if (Number.isFinite(parsed)) onValueChange(parsed);
              }}
              onBlur={() => {
                setDraft(null);
                if (value !== null && clamp(value) !== value) onValueChange(clamp(value));
              }}
              onKeyDown={onKeyDown}
            />
          </InputGroup>
        </span>
      )}
    </Field>
  );
}

/**
 * One-time code field (spec 036 DLS-035): one box per character, for codes sent by email or an authenticator. Typing
 * moves on, Backspace moves back, the arrow keys move along the reading direction, and a pasted code fills every box.
 * The boxes are one group named by the label; the whole code is submitted under `name`. Codes are always written left
 * to right, also in a right-to-left page.
 */
export function OtpField({
  label,
  hint,
  error,
  name,
  length = 6,
  value,
  onValueChange,
  onComplete,
}: FieldText & { name: string; length?: number; value: string; onValueChange: (value: string) => void; onComplete?: (code: string) => void }) {
  const t = useTranslations("ui.controls");
  const id = useId();
  const boxes = useRef<(HTMLInputElement | null)[]>([]);
  const digits = Array.from({ length }, (_, i) => value[i] ?? "");
  const describedBy = [hint ? `${id}-hint` : null, error ? `${id}-error` : null].filter(Boolean).join(" ") || undefined;

  function write(next: string, focus: number) {
    const clean = next.replace(/\D/g, "").slice(0, length);
    onValueChange(clean);
    boxes.current[Math.min(focus, length - 1)]?.focus();
    if (clean.length === length) onComplete?.(clean);
  }

  function onKeyDown(index: number, event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === "Backspace") {
      event.preventDefault();
      const at = digits[index] ? index : Math.max(index - 1, 0);
      write(value.slice(0, at) + value.slice(at + 1), at);
    } else if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
      event.preventDefault();
      boxes.current[Math.max(0, Math.min(length - 1, index + (event.key === "ArrowRight" ? 1 : -1)))]?.focus();
    } else if (/^\d$/.test(event.key)) {
      event.preventDefault();
      const at = Math.min(index, value.length);
      write(value.slice(0, at) + event.key + value.slice(at + 1), at + 1);
    }
  }

  function onPaste(event: ClipboardEvent<HTMLInputElement>) {
    event.preventDefault();
    const pasted = event.clipboardData.getData("text");
    write(pasted, pasted.replace(/\D/g, "").length);
  }

  return (
    <div className="field">
      <span id={`${id}-label`} className="ui-field-label">
        {label}
      </span>
      {hint ? (
        <p id={`${id}-hint`} className="field-hint">
          {hint}
        </p>
      ) : null}
      <div role="group" aria-labelledby={`${id}-label`} aria-describedby={describedBy} className="ui-otp" dir="ltr">
        {digits.map((digit, index) => (
          <input
            key={index}
            ref={(el) => {
              boxes.current[index] = el;
            }}
            type="text"
            inputMode="numeric"
            autoComplete={index === 0 ? "one-time-code" : "off"}
            maxLength={1}
            aria-label={t("digit", { index: index + 1, count: length })}
            aria-invalid={error ? true : undefined}
            value={digit}
            // Typing is handled on key down; this covers input methods and autofill that send no key.
            onChange={(event) => write(value.slice(0, index) + event.currentTarget.value + value.slice(index + 1), index + 1)}
            onKeyDown={(event) => onKeyDown(index, event)}
            onPaste={onPaste}
            onFocus={(event) => event.currentTarget.select()}
          />
        ))}
      </div>
      <input type="hidden" name={name} value={value} />
      {error ? (
        <p id={`${id}-error`} className="field-error" role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
}

/**
 * Time field (spec 036 DLS-035): the browser's own time input, which already writes the time in the person's format,
 * steps each part with the arrow keys and is read correctly by screen readers. The value is "HH:mm" in 24 hours.
 */
export function TimeField({ label, hint, error, ...input }: Omit<InputHTMLAttributes<HTMLInputElement>, "type"> & FieldText & { name: string }) {
  return (
    <Field label={label} hint={hint} error={error}>
      {({ id, describedBy, invalid }) => <input id={id} type="time" aria-invalid={invalid} aria-describedby={describedBy} {...input} />}
    </Field>
  );
}

/**
 * Color field (spec 036 DLS-035): a hex code, the browser's color picker and optional named presets. The code is text,
 * so it can be pasted and read aloud; presets are buttons named by their color, never a bare swatch. Whether a color
 * is allowed (contrast, brand rules) is decided by the API, and the result arrives as `error`.
 */
export function ColorField({
  label,
  hint,
  error,
  name,
  value,
  onValueChange,
  presets = [],
}: FieldText & { name: string; value: string; onValueChange: (value: string) => void; presets?: readonly { value: string; label: string }[] }) {
  const t = useTranslations("ui.controls");
  const valid = /^#[0-9a-f]{6}$/i.test(value);
  return (
    <Field label={label} hint={hint} error={error}>
      {({ id, describedBy, invalid }) => (
        <span className="ui-color">
          <InputGroup
            leading={
              <input
                type="color"
                className="ui-color-swatch"
                aria-label={t("pickColor", { label })}
                value={valid ? value : "#000000"}
                onChange={(event) => onValueChange(event.currentTarget.value)}
              />
            }
          >
            <input
              id={id}
              name={name}
              type="text"
              dir="ltr"
              spellCheck={false}
              autoComplete="off"
              maxLength={7}
              aria-invalid={invalid}
              aria-describedby={describedBy}
              value={value}
              onChange={(event) => onValueChange(event.currentTarget.value)}
            />
          </InputGroup>
          {presets.length > 0 ? (
            <span role="group" aria-label={t("presets", { label })} className="ui-color-presets">
              {presets.map((preset) => (
                <button
                  key={preset.value}
                  type="button"
                  className="ui-color-preset"
                  aria-label={preset.label}
                  title={preset.label}
                  aria-pressed={preset.value.toLowerCase() === value.toLowerCase()}
                  style={{ color: preset.value }}
                  onClick={() => onValueChange(preset.value)}
                />
              ))}
            </span>
          ) : null}
        </span>
      )}
    </Field>
  );
}
