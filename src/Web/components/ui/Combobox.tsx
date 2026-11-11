"use client";

import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { Field, InputGroup } from "./FormField";
import { Icon } from "./Icon";
import { place } from "./overlay";

export type ComboboxOption = { value: string; label: string; hint?: string };

/**
 * Combobox (spec 036 DLS-036): one choice from a long list, found by typing. It follows the editable combobox pattern
 * with a list: focus stays in the text box, the arrow keys move the highlighted option, Enter chooses it and Escape
 * closes the list, then clears the text. The value is always one of the options or nothing; text that matches no
 * option is put back when the field is left. A caller that searches on the server passes `onQueryChange` and gives
 * back the options to show.
 */
export function Combobox({
  label,
  hint,
  error,
  name,
  options,
  value,
  onValueChange,
  onQueryChange,
  empty,
  placeholder,
  disabled,
}: {
  label: string;
  hint?: string;
  error?: string | null;
  name: string;
  options: readonly ComboboxOption[];
  value: string | null;
  onValueChange: (value: string | null) => void;
  onQueryChange?: (query: string) => void;
  /** Shown in the list when nothing matches. */
  empty: string;
  placeholder?: string;
  disabled?: boolean;
}) {
  const listId = useId();
  const input = useRef<HTMLInputElement>(null);
  const group = useRef<HTMLSpanElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const selected = options.find((o) => o.value === value) ?? null;
  const [query, setQuery] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const text = query ?? selected?.label ?? "";

  const shown = useMemo(() => {
    const term = (query ?? "").trim().toLocaleLowerCase();
    if (!term || onQueryChange) return options;
    return options.filter((o) => `${o.label} ${o.hint ?? ""}`.toLocaleLowerCase().includes(term));
  }, [options, query, onQueryChange]);
  const current = Math.min(active, Math.max(shown.length - 1, 0));
  const optionId = (index: number) => `${listId}-${index}`;

  // The list is a popover, so it is never clipped by a dialog or a scrolling panel.
  useEffect(() => {
    const el = list.current;
    if (!el) return;
    if (open && !el.matches(":popover-open")) el.showPopover();
    if (!open && el.matches(":popover-open")) el.hidePopover();
    if (open && group.current) {
      el.style.inlineSize = `${group.current.offsetWidth}px`;
      place(el, group.current.getBoundingClientRect(), "bottom-start");
    }
  }, [open, shown.length]);

  function choose(option: ComboboxOption | undefined) {
    if (!option) return;
    onValueChange(option.value);
    setQuery(null);
    setOpen(false);
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    const last = shown.length - 1;
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      if (!open) {
        setOpen(true);
        return;
      }
      if (last < 0) return;
      const next = event.key === "ArrowDown" ? (current >= last ? 0 : current + 1) : current <= 0 ? last : current - 1;
      setActive(next);
      document.getElementById(optionId(next))?.scrollIntoView({ block: "nearest" });
    } else if (event.key === "Enter" && open) {
      event.preventDefault();
      choose(shown[current]);
    } else if (event.key === "Escape") {
      if (open) {
        event.preventDefault();
        setOpen(false);
      } else if (text) {
        event.preventDefault();
        setQuery(null);
        onValueChange(null);
      }
    }
  }

  return (
    <Field label={label} hint={hint} error={error}>
      {({ id, describedBy, invalid }) => (
        <>
          <span ref={group} className="ui-combobox">
            <InputGroup trailing={<Icon name="chevron-down" size={16} />}>
              <input
                ref={input}
                id={id}
                type="text"
                role="combobox"
                aria-expanded={open}
                aria-controls={listId}
                aria-autocomplete="list"
                aria-activedescendant={open && shown.length > 0 ? optionId(current) : undefined}
                aria-invalid={invalid}
                aria-describedby={describedBy}
                autoComplete="off"
                spellCheck={false}
                placeholder={placeholder}
                disabled={disabled}
                value={text}
                onChange={(event) => {
                  const next = event.currentTarget.value;
                  setQuery(next);
                  setActive(0);
                  setOpen(true);
                  onQueryChange?.(next);
                  if (next === "") onValueChange(null);
                }}
                onClick={() => setOpen(true)}
                onKeyDown={onKeyDown}
                onBlur={() => {
                  setOpen(false);
                  setQuery(null);
                }}
              />
            </InputGroup>
          </span>
          <div ref={list} id={listId} popover="manual" role="listbox" aria-label={label} className="ui-menu-popover ui-combobox-list">
            {shown.map((option, index) => (
              <div
                key={option.value}
                id={optionId(index)}
                role="option"
                aria-selected={index === current}
                className="ui-palette-option"
                // Keeps focus in the text box, so the click is not lost to the blur that closes the list.
                onPointerDown={(event) => event.preventDefault()}
                onPointerMove={() => setActive(index)}
                onClick={() => choose(option)}
              >
                <span className="ui-palette-text">
                  <span className="ui-menu-label">{option.label}</span>
                  {option.hint ? <span className="ui-palette-hint">{option.hint}</span> : null}
                </span>
                {option.value === value ? <Icon name="check" size={16} /> : null}
              </div>
            ))}
            {shown.length === 0 ? <p className="ui-combobox-empty">{empty}</p> : null}
          </div>
          <input type="hidden" name={name} value={value ?? ""} />
        </>
      )}
    </Field>
  );
}
