"use client";

import { useId, useMemo, useState, type ReactNode } from "react";
import { useTranslations } from "next-intl";
import { Checkbox } from "./Checkbox";
import { Icon } from "./Icon";
import { Menu } from "./Menu";
import { StateView } from "./StateView";

export type SortDirection = "ascending" | "descending";
export type Sort = { key: string; direction: SortDirection };

export type Column<Row> = {
  key: string;
  header: string;
  cell: (row: Row) => ReactNode;
  /** Makes the column sortable. Sorting here only orders the rows already shown; a feature that pages through the API sorts there and passes `manualSort`. */
  sortValue?: (row: Row) => string | number;
  /** Numbers and amounts line up at the end. */
  align?: "start" | "end";
  /** The cell that names the row; it becomes the row header. */
  rowHeader?: boolean;
  /** A column the person can hide from the columns menu. Columns that name the row cannot be hidden. */
  hideable?: boolean;
  /** Low priority columns are left out below 1024px, so the key fields fit (DLS-079). */
  priority?: "low";
};

type Props<Row> = {
  caption: string;
  columns: readonly Column<Row>[];
  rows: readonly Row[];
  rowKey: (row: Row) => string;
  /** The accessible name of a row's checkbox, such as the request title. Turns on row selection. */
  rowLabel?: (row: Row) => string;
  selected?: readonly string[];
  onSelectedChange?: (keys: string[]) => void;
  sort?: Sort | null;
  defaultSort?: Sort | null;
  onSortChange?: (sort: Sort) => void;
  manualSort?: boolean;
  hidden?: readonly string[];
  onHiddenChange?: (keys: string[]) => void;
  /** Search, filters and actions above the table. The columns menu is added at the end. */
  toolbar?: ReactNode;
  /** Shown above the table when rows are selected, such as a selection bar. */
  selection?: ReactNode;
  /** Paging and the range text below the table. */
  footer?: ReactNode;
  emptyTitle?: string;
  emptyMessage?: string;
  density?: "comfortable" | "compact";
  /** Below 640px each row is a stacked card of label and value pairs (DLS-079). Off for grids that must keep columns. */
  stack?: boolean;
};

function compare(a: string | number, b: string | number) {
  if (typeof a === "number" && typeof b === "number") return a - b;
  return String(a).localeCompare(String(b));
}

/**
 * Data table (spec 036 DLS-038, DLS-039): a native table with a caption, sortable headers that state their order,
 * optional row selection with a mixed state on the header box, a columns menu and slots for a toolbar and paging. It
 * holds no business rule: rows, filters and actions come from the feature.
 */
export function DataTable<Row>({
  caption,
  columns,
  rows,
  rowKey,
  rowLabel,
  selected = [],
  onSelectedChange,
  sort: sortProp,
  defaultSort = null,
  onSortChange,
  manualSort = false,
  hidden: hiddenProp,
  onHiddenChange,
  toolbar,
  selection,
  footer,
  emptyTitle,
  emptyMessage,
  density = "comfortable",
  stack = true,
}: Props<Row>) {
  const t = useTranslations("ui.data");
  const id = useId();
  const [innerSort, setInnerSort] = useState<Sort | null>(defaultSort);
  const [innerHidden, setInnerHidden] = useState<readonly string[]>([]);
  const sort = sortProp === undefined ? innerSort : sortProp;
  const hidden = hiddenProp ?? innerHidden;
  const shown = columns.filter((c) => !hidden.includes(c.key));
  const selectable = Boolean(rowLabel && onSelectedChange);

  const ordered = useMemo(() => {
    const column = columns.find((c) => c.key === sort?.key);
    if (manualSort || !sort || !column?.sortValue) return rows;
    const value = column.sortValue;
    const sign = sort.direction === "ascending" ? 1 : -1;
    return [...rows].sort((a, b) => sign * compare(value(a), value(b)));
  }, [rows, columns, sort, manualSort]);

  function sortBy(key: string) {
    const next: Sort = { key, direction: sort?.key === key && sort.direction === "ascending" ? "descending" : "ascending" };
    setInnerSort(next);
    onSortChange?.(next);
  }

  function setHidden(keys: string[]) {
    setInnerHidden(keys);
    onHiddenChange?.(keys);
  }

  const keys = ordered.map(rowKey);
  const chosen = keys.filter((k) => selected.includes(k)).length;
  const hideable = columns.filter((c) => c.hideable && !c.rowHeader);

  function toggleAll() {
    onSelectedChange?.(chosen === keys.length ? selected.filter((k) => !keys.includes(k)) : [...new Set([...selected, ...keys])]);
  }

  function toggle(key: string) {
    onSelectedChange?.(selected.includes(key) ? selected.filter((k) => k !== key) : [...selected, key]);
  }

  return (
    <div className="ui-table" data-density={density === "compact" ? density : undefined}>
      {toolbar || hideable.length ? (
        <div className="ui-table-toolbar">
          {toolbar}
          {hideable.length ? (
            <div className="ui-table-tools">
              <Menu
                label={t("columns")}
                icon="settings"
                size="sm"
                placement="bottom-end"
                items={[
                  {
                    kind: "group",
                    key: "columns",
                    label: t("showColumns"),
                    items: hideable.map((c) => ({
                      key: c.key,
                      label: c.header,
                      checked: !hidden.includes(c.key),
                      onSelect: () => setHidden(hidden.includes(c.key) ? hidden.filter((k) => k !== c.key) : [...hidden, c.key]),
                    })),
                  },
                ]}
              />
            </div>
          ) : null}
        </div>
      ) : null}
      {selection}
      <div className="ui-table-scroll" role="region" aria-labelledby={`${id}-caption`} tabIndex={0}>
        <table data-stack={stack || undefined}>
          <caption id={`${id}-caption`}>{caption}</caption>
          <thead>
            <tr>
              {selectable ? (
                <th scope="col" className="ui-table-check">
                  <Checkbox
                    label={<span className="visually-hidden">{t("selectAll")}</span>}
                    checked={keys.length > 0 && chosen === keys.length}
                    indeterminate={chosen > 0 && chosen < keys.length}
                    disabled={keys.length === 0}
                    onChange={toggleAll}
                  />
                </th>
              ) : null}
              {shown.map((c) => {
                const direction = sort?.key === c.key ? sort.direction : undefined;
                return (
                  <th key={c.key} scope="col" data-align={c.align === "end" ? "end" : undefined} data-priority={c.priority} aria-sort={c.sortValue ? (direction ?? "none") : undefined}>
                    {c.sortValue ? (
                      <button type="button" className="ui-table-sort" data-direction={direction} onClick={() => sortBy(c.key)}>
                        <span>{c.header}</span>
                        <Icon name={direction ? "chevron-down" : "sort"} size={14} />
                      </button>
                    ) : (
                      c.header
                    )}
                  </th>
                );
              })}
            </tr>
          </thead>
          <tbody>
            {ordered.length === 0 ? (
              <tr>
                <td colSpan={shown.length + (selectable ? 1 : 0)}>
                  <StateView kind="empty" title={emptyTitle} message={emptyMessage} />
                </td>
              </tr>
            ) : (
              ordered.map((row) => {
                const key = rowKey(row);
                const isSelected = selected.includes(key);
                return (
                  <tr key={key} data-selected={isSelected || undefined}>
                    {selectable && rowLabel ? (
                      <td className="ui-table-check">
                        <Checkbox label={<span className="visually-hidden">{rowLabel(row)}</span>} checked={isSelected} onChange={() => toggle(key)} />
                      </td>
                    ) : null}
                    {shown.map((c) =>
                      c.rowHeader ? (
                        <th key={c.key} scope="row" data-align={c.align === "end" ? "end" : undefined} data-priority={c.priority}>
                          {c.cell(row)}
                        </th>
                      ) : (
                        <td key={c.key} data-align={c.align === "end" ? "end" : undefined} data-priority={c.priority} data-label={c.header}>
                          {c.cell(row)}
                        </td>
                      ),
                    )}
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>
      {footer ? <div className="ui-table-footer">{footer}</div> : null}
    </div>
  );
}
