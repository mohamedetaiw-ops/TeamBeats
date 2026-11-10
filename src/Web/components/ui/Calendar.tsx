"use client";

import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useNow } from "@/lib/clock";
import { Button } from "./Button";
import { Field } from "./FormField";
import { Icon } from "./Icon";
import { addDays, addMonths, firstWeekday, fromIso, inRange, localIso, monthGrid, monthStart, weekdayOf, type CalendarSystem, type DateRange } from "./dates";
import { usePopover } from "./overlay";

type Shared = {
  /** The name of the grid, such as "Leave start". */
  label: string;
  min?: string;
  max?: string;
  /** Days that cannot be chosen, such as public holidays. The rule comes from the feature's data, not from here. */
  isUnavailable?: (iso: string) => boolean;
  /** The tenant's calendar. Days are always stored as ISO days; only what people read and how months turn changes. */
  calendar?: CalendarSystem;
  /** 0 for Sunday. By default the first day of the week of the locale. */
  weekStart?: number;
  /** The month to show when nothing is chosen. By default the current month. */
  defaultMonth?: string;
  /** The viewer's day, when the caller already knows it (the server does, from the tenant's time zone). By default the browser's. */
  today?: string;
};
type Single = Shared & { value: string | null; onValueChange: (iso: string) => void; range?: undefined; onRangeChange?: undefined };
type Ranged = Shared & { range: DateRange; onRangeChange: (range: DateRange) => void; value?: undefined; onValueChange?: undefined };

/**
 * Calendar (spec 036 DLS-036): one month as a grid of days, for one day or a range. It follows the date grid pattern:
 * the arrow keys move by day and week in the reading direction, Home and End go to the ends of the week, Page Up and
 * Page Down change the month and, with Shift, the year. Month names, weekday names and digits come from `Intl` in the
 * person's locale and the tenant's calendar, including the Hijri one. Today is marked, and never by color alone.
 */
export function Calendar(props: Single | Ranged) {
  const { label, min, max, isUnavailable, calendar = "gregory", defaultMonth } = props;
  const t = useTranslations("ui.controls");
  const locale = useLocale();
  const now = useNow();
  const today = props.today ?? (now === null ? null : localIso(now));
  const titleId = useId();
  const table = useRef<HTMLTableElement>(null);
  const moveFocus = useRef(false);
  const [cursor, setCursor] = useState<string | null>(null);
  const chosen = props.range ? props.range.start : props.value;
  const focused = cursor ?? chosen ?? defaultMonth ?? today;
  const weekStart = props.weekStart ?? firstWeekday(locale);

  const formats = useMemo(() => {
    const options = { calendar, timeZone: "UTC" } as const;
    // Short weekday names fit the columns in some languages ("Mon"); where they are whole words, the narrow form is used.
    const probe = new Intl.DateTimeFormat(locale, { weekday: "short", timeZone: "UTC" });
    const short = Array.from({ length: 7 }, (_, i) => probe.format(Date.UTC(2026, 0, 4 + i))).every((name) => name.length <= 3);
    return {
      title: new Intl.DateTimeFormat(locale, { ...options, month: "long", year: "numeric" }),
      day: new Intl.DateTimeFormat(locale, { ...options, day: "numeric" }),
      full: new Intl.DateTimeFormat(locale, { ...options, dateStyle: "full" }),
      weekday: new Intl.DateTimeFormat(locale, { weekday: short ? "short" : "narrow", timeZone: "UTC" }),
      weekdayLong: new Intl.DateTimeFormat(locale, { weekday: "long", timeZone: "UTC" }),
    };
  }, [locale, calendar]);

  useEffect(() => {
    if (!moveFocus.current || !focused) return;
    moveFocus.current = false;
    table.current?.querySelector<HTMLElement>(`[data-date="${focused}"]`)?.focus();
  }, [focused]);

  if (!focused) {
    // The server does not know the viewer's day; the grid appears once the browser does.
    return <div className="ui-calendar" aria-busy="true" />;
  }

  const start = monthStart(focused, calendar);
  const weeks = monthGrid(start, calendar, weekStart);
  // 4 January 2026 is a Sunday, so day 4 + n is weekday n.
  const weekdays = Array.from({ length: 7 }, (_, i) => Date.UTC(2026, 0, 4 + ((weekStart + i) % 7)));
  const allowed = (iso: string) => inRange(iso, min, max) && !isUnavailable?.(iso);

  function go(iso: string, focus = true) {
    const next = min && iso < min ? min : max && iso > max ? max : iso;
    moveFocus.current = focus;
    setCursor(next);
  }

  function choose(iso: string) {
    if (!allowed(iso)) return;
    setCursor(iso);
    if (!props.range) props.onValueChange(iso);
    else if (!props.range.start || props.range.end) props.onRangeChange({ start: iso, end: null });
    else props.onRangeChange(iso < props.range.start ? { start: iso, end: props.range.start } : { start: props.range.start, end: iso });
  }

  function onKeyDown(event: KeyboardEvent<HTMLTableElement>) {
    if (!focused) return;
    const rtl = getComputedStyle(event.currentTarget).direction === "rtl";
    const fromWeekStart = (weekdayOf(focused) - weekStart + 7) % 7;
    const moves: Record<string, () => string> = {
      ArrowRight: () => addDays(focused, rtl ? -1 : 1),
      ArrowLeft: () => addDays(focused, rtl ? 1 : -1),
      ArrowDown: () => addDays(focused, 7),
      ArrowUp: () => addDays(focused, -7),
      Home: () => addDays(focused, -fromWeekStart),
      End: () => addDays(focused, 6 - fromWeekStart),
      PageDown: () => addMonths(focused, event.shiftKey ? 12 : 1, calendar),
      PageUp: () => addMonths(focused, event.shiftKey ? -12 : -1, calendar),
    };
    const move = moves[event.key];
    if (!move) return;
    event.preventDefault();
    go(move());
  }

  const state = (iso: string) => {
    if (!props.range) return iso === props.value ? "selected" : undefined;
    const { start: from, end: to } = props.range;
    if (iso === from) return to && to !== from ? "start" : "selected";
    if (iso === to) return "end";
    return from && to && iso > from && iso < to ? "middle" : undefined;
  };

  return (
    <div className="ui-calendar">
      <div className="ui-calendar-head">
        <button type="button" className="button button-quiet ui-icon-button ui-calendar-previous" data-size="sm" aria-label={t("previousMonth")} onClick={() => go(addMonths(focused, -1, calendar), false)}>
          <Icon name="chevron-forward" size={18} />
        </button>
        <p id={titleId} className="ui-calendar-title" aria-live="polite">
          {formats.title.format(fromIso(start))}
        </p>
        <button type="button" className="button button-quiet ui-icon-button" data-size="sm" aria-label={t("nextMonth")} onClick={() => go(addMonths(focused, 1, calendar), false)}>
          <Icon name="chevron-forward" size={18} />
        </button>
      </div>
      <table ref={table} role="grid" aria-label={label} aria-describedby={titleId} aria-multiselectable={props.range ? true : undefined} onKeyDown={onKeyDown}>
        <thead>
          <tr>
            {weekdays.map((ms) => (
              <th key={ms} scope="col" abbr={formats.weekdayLong.format(ms)}>
                {formats.weekday.format(ms)}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {weeks.map((week, w) => (
            <tr key={w}>
              {week.map((iso, d) => {
                if (!iso) return <td key={d} />;
                const mark = state(iso);
                return (
                  <td key={d} role="gridcell" aria-selected={mark !== undefined} data-range={mark}>
                    <button
                      type="button"
                      className="ui-calendar-day"
                      data-date={iso}
                      tabIndex={iso === focused ? 0 : -1}
                      aria-label={formats.full.format(fromIso(iso))}
                      aria-current={iso === today ? "date" : undefined}
                      aria-disabled={allowed(iso) ? undefined : true}
                      onClick={() => choose(iso)}
                    >
                      {formats.day.format(fromIso(iso))}
                    </button>
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

type FieldProps = { hint?: string; error?: string | null; name: string; placeholder: string; clearLabel?: string; footer?: ReactNode };

function PickerField({
  label,
  hint,
  error,
  text,
  placeholder,
  children,
}: {
  label: string;
  hint?: string;
  error?: string | null;
  text: string | null;
  placeholder: string;
  children: (close: () => void) => ReactNode;
}) {
  const { trigger, panel, open, hide, onPointerDown, onClick } = usePopover("bottom-start", focusDay);
  const panelId = useId();
  return (
    <Field label={label} hint={hint} error={error}>
      {({ id, describedBy, invalid }) => (
        <>
          <button
            ref={trigger}
            id={id}
            type="button"
            className="ui-date-button"
            data-empty={text ? undefined : true}
            aria-haspopup="dialog"
            aria-expanded={open}
            aria-controls={panelId}
            data-invalid={invalid}
            aria-describedby={describedBy}
            onPointerDown={onPointerDown}
            onClick={onClick}
          >
            <span>{text ?? placeholder}</span>
            <Icon name="calendar" size={18} />
          </button>
          <div ref={panel} id={panelId} popover="auto" role="dialog" aria-label={label} className="ui-popover ui-date-popover">
            {children(hide)}
          </div>
        </>
      )}
    </Field>
  );
}

/** Moves focus to the day the grid would give to Tab, when the picker opens. */
function focusDay(panel: HTMLElement) {
  panel.querySelector<HTMLElement>('.ui-calendar-day[tabindex="0"]')?.focus();
}

/**
 * Date field (spec 036 DLS-036): a field that shows the chosen day in the person's own words and opens a calendar.
 * The value under `name` is the ISO day. Choosing a day closes the calendar and returns focus to the field.
 */
export function DateField({
  label,
  hint,
  error,
  name,
  placeholder,
  clearLabel,
  value,
  onValueChange,
  ...calendar
}: Omit<Shared, "label"> & FieldProps & { label: string; value: string | null; onValueChange: (iso: string | null) => void }) {
  const locale = useLocale();
  const format = useMemo(() => new Intl.DateTimeFormat(locale, { calendar: calendar.calendar ?? "gregory", timeZone: "UTC", dateStyle: "long" }), [locale, calendar.calendar]);
  return (
    <>
      <PickerField label={label} hint={hint} error={error} placeholder={placeholder} text={value ? format.format(fromIso(value)) : null}>
        {(close) => (
          <>
            <Calendar
              {...calendar}
              label={label}
              value={value}
              onValueChange={(iso) => {
                onValueChange(iso);
                close();
              }}
            />
            {clearLabel && value ? (
              <Button
                size="sm"
                variant="quiet"
                onClick={() => {
                  onValueChange(null);
                  close();
                }}
              >
                {clearLabel}
              </Button>
            ) : null}
          </>
        )}
      </PickerField>
      <input type="hidden" name={name} value={value ?? ""} />
    </>
  );
}

/**
 * Date range field (spec 036 DLS-036): the same field for a first and a last day. The first choice starts the range
 * and the second ends it; the calendar closes when both are set. The values go out as `name` + "_start" and "_end".
 */
export function DateRangeField({
  label,
  hint,
  error,
  name,
  placeholder,
  clearLabel,
  range,
  onRangeChange,
  ...calendar
}: Omit<Shared, "label"> & FieldProps & { label: string; range: DateRange; onRangeChange: (range: DateRange) => void }) {
  const locale = useLocale();
  const t = useTranslations("ui.controls");
  const format = useMemo(
    () => new Intl.DateTimeFormat(locale, { calendar: calendar.calendar ?? "gregory", timeZone: "UTC", day: "numeric", month: "short", year: "numeric" }),
    [locale, calendar.calendar],
  );
  // The two days are joined by a message, not by `formatRange`, whose spacing differs between the server and browsers.
  const first = range.start ? format.format(fromIso(range.start)) : null;
  const text = first && range.end ? t("dateRange", { start: first, end: format.format(fromIso(range.end)) }) : first;
  return (
    <>
      <PickerField label={label} hint={hint} error={error} placeholder={placeholder} text={text}>
        {(close) => (
          <>
            <Calendar
              {...calendar}
              label={label}
              range={range}
              onRangeChange={(next) => {
                onRangeChange(next);
                if (next.start && next.end) close();
              }}
            />
            {clearLabel && range.start ? (
              <Button size="sm" variant="quiet" onClick={() => onRangeChange({ start: null, end: null })}>
                {clearLabel}
              </Button>
            ) : null}
          </>
        )}
      </PickerField>
      <input type="hidden" name={`${name}_start`} value={range.start ?? ""} />
      <input type="hidden" name={`${name}_end`} value={range.end ?? ""} />
    </>
  );
}

/**
 * Slot picker (spec 036 DLS-036): the free times of one day, for booking a room, a desk or a meeting. It is a radio
 * group, so the arrow keys move the choice; a taken slot stays visible and says why it cannot be chosen. Times are
 * written by `Intl` in the person's format. Which slots exist and which are free comes from the feature.
 */
export function SlotPicker({
  legend,
  name,
  slots,
  value,
  onValueChange,
  empty,
}: {
  legend: string;
  name: string;
  /** `time` is "HH:mm" in 24 hours; `note` says why a slot is unavailable, such as "Booked". */
  slots: readonly { time: string; unavailable?: boolean; note?: string }[];
  value: string | null;
  onValueChange: (time: string) => void;
  /** Shown when the day has no slots. */
  empty: string;
}) {
  const locale = useLocale();
  const format = useMemo(() => new Intl.DateTimeFormat(locale, { hour: "numeric", minute: "2-digit", timeZone: "UTC" }), [locale]);
  const text = (time: string) => format.format(Date.parse(`2026-01-01T${time}:00Z`));
  return (
    <fieldset className="ui-slots">
      <legend>{legend}</legend>
      {slots.length === 0 ? <p className="notice">{empty}</p> : null}
      <div className="ui-slots-list">
        {slots.map((slot) => (
          <label key={slot.time} className="ui-slot">
            <input type="radio" name={name} value={slot.time} checked={slot.time === value} disabled={slot.unavailable} onChange={() => onValueChange(slot.time)} />
            <span>
              {text(slot.time)}
              {slot.note ? <span className="ui-slot-note">{slot.note}</span> : null}
            </span>
          </label>
        ))}
      </div>
    </fieldset>
  );
}
