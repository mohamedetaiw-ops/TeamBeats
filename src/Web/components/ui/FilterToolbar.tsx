"use client";

import Link from "next/link";
import { useId, useState, type ComponentPropsWithoutRef, type FormEvent, type ReactNode } from "react";
import { useTranslations } from "next-intl";
import { Icon } from "./Icon";

/** A filter in force, shown as a removable chip under the toolbar. A server page passes `href`, a client list `onRemove`. */
export type AppliedFilter = { key: string; label: string; href?: string; onRemove?: () => void };

/**
 * Filter toolbar (spec 036 DLS-106): the one way to search and filter a list. One row, not a card: the search box
 * first, then at most three compact filters, then view and sort controls at the end. Filters in force show as
 * removable chips under the row with "Clear all". On a phone the search box stays and the filters open from a
 * Filters button that carries the count of filters in force.
 *
 * It works as a plain GET form on a server page (`action`, `method`, chips as links) and as a client form
 * (`onSubmit`, chips with `onRemove`).
 */
export function FilterToolbar({
  label,
  search,
  children,
  end,
  applied = [],
  clearHref,
  onClear,
  action,
  method,
  onSubmit,
}: {
  /** The accessible name of the search form, such as "Search people". */
  label: string;
  search?: ReactNode;
  /** The filters: `ToolbarSelect` or a segmented control. */
  children?: ReactNode;
  /** View and sort controls, and the one compact submit button of a list that needs one. */
  end?: ReactNode;
  applied?: readonly AppliedFilter[];
  clearHref?: string;
  onClear?: () => void;
  action?: string;
  method?: "get";
  onSubmit?: (event: FormEvent<HTMLFormElement>) => void;
}) {
  const t = useTranslations("ui.controls");
  const id = useId();
  const [open, setOpen] = useState(false);
  return (
    <div className="ui-toolbar-wrap">
      <form role="search" aria-label={label} className="ui-toolbar" data-open={open || undefined} action={action} method={method} onSubmit={onSubmit}>
        {search}
        {children ? (
          <>
            <button type="button" className="button button-secondary ui-toolbar-toggle" aria-expanded={open} aria-controls={id} onClick={() => setOpen(!open)}>
              <Icon name="filter" size={16} />
              <span>{t("filters")}</span>
              {applied.length > 0 ? <span className="ui-toolbar-count">{applied.length}</span> : null}
            </button>
            <div id={id} className="ui-toolbar-filters">
              {children}
            </div>
          </>
        ) : null}
        {end ? <div className="ui-toolbar-end">{end}</div> : null}
      </form>
      {applied.length > 0 ? (
        <ul className="ui-toolbar-applied" aria-label={t("appliedFilters")}>
          {applied.map((f) => (
            <li key={f.key}>
              {f.href ? (
                <Link className="ui-chip ui-toolbar-chip" href={f.href} aria-label={t("remove", { name: f.label })}>
                  <span>{f.label}</span>
                  <Icon name="close" size={14} />
                </Link>
              ) : (
                <span className="ui-chip">
                  <span>{f.label}</span>
                  <button type="button" className="ui-chip-remove" aria-label={t("remove", { name: f.label })} onClick={f.onRemove}>
                    <Icon name="close" size={14} />
                  </button>
                </span>
              )}
            </li>
          ))}
          {clearHref ? (
            <li>
              <Link className="ui-toolbar-clear" href={clearHref}>
                {t("clearAll")}
              </Link>
            </li>
          ) : onClear ? (
            <li>
              <button type="button" className="ui-toolbar-clear" onClick={onClear}>
                {t("clearAll")}
              </button>
            </li>
          ) : null}
        </ul>
      ) : null}
    </div>
  );
}

/** The search box of a toolbar: the label is read but not shown, and the hint is the placeholder. */
export function ToolbarSearch({ label, ...input }: { label: string } & Omit<ComponentPropsWithoutRef<"input">, "type">) {
  return (
    <label className="ui-toolbar-search">
      <span className="visually-hidden">{label}</span>
      <Icon name="search" size={16} />
      <input type="search" dir="auto" {...input} />
    </label>
  );
}

/**
 * A compact filter: the name sits inside the control before the value, so "All" is never left unexplained. Pass
 * `hideLabel` when the empty choice already names the filter ("Any status").
 */
export function ToolbarSelect({
  label,
  hideLabel = false,
  children,
  ...select
}: { label: string; hideLabel?: boolean; children: ReactNode } & ComponentPropsWithoutRef<"select">) {
  return (
    <label className="ui-toolbar-select">
      <span className={hideLabel ? "visually-hidden" : undefined}>{label}</span>
      <select {...select}>{children}</select>
    </label>
  );
}
