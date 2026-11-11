"use client";

import { useEffect, useId, useRef, useState, type FocusEvent, type KeyboardEvent } from "react";
import type { Column } from "./DataTable";

const focusable = 'a[href], button, input, select, textarea, [tabindex]:not([tabindex="-1"]):not(td):not(th)';

/** Takes the controls inside cells out of the tab order, or puts the ones of one cell back. */
function setControls(cell: Element, tabbable: boolean) {
  for (const el of cell.querySelectorAll<HTMLElement>(focusable)) el.tabIndex = tabbable ? 0 : -1;
}

/**
 * Data grid (spec 036 DLS-039): a table that works like a spreadsheet, for editing many values in place, such as a
 * budget or a roster. It follows the grid pattern: the grid is one stop in the tab order; the arrows move between
 * cells following the reading direction; Home and End go to the ends of the row and, with Control, of the grid. Enter
 * or F2 moves into the controls of a cell and Escape comes back to the cell. Use the data table to read lists.
 */
export function DataGrid<Row>({
  caption,
  columns,
  rows,
  rowKey,
}: {
  caption: string;
  columns: readonly Column<Row>[];
  rows: readonly Row[];
  rowKey: (row: Row) => string;
}) {
  const id = useId();
  const grid = useRef<HTMLTableElement>(null);
  const [cursor, setCursor] = useState<[number, number]>([0, 0]);

  useEffect(() => {
    const table = grid.current;
    if (!table) return;
    // The cell being edited keeps its controls in the tab order while the feature re-renders.
    const editing = document.activeElement?.closest("td, th");
    for (const cell of table.querySelectorAll('[role="gridcell"], [role="columnheader"], [role="rowheader"]')) {
      if (cell !== editing || document.activeElement === cell) setControls(cell, false);
    }
  });

  function cellAt(row: number, column: number) {
    return grid.current?.rows[row]?.cells[column] ?? null;
  }

  function move(row: number, column: number) {
    const table = grid.current;
    if (!table) return;
    const r = Math.max(0, Math.min(row, table.rows.length - 1));
    const c = Math.max(0, Math.min(column, columns.length - 1));
    setCursor([r, c]);
    cellAt(r, c)?.focus();
  }

  function onKeyDown(event: KeyboardEvent<HTMLTableElement>) {
    const target = event.target as HTMLElement;
    const cell = target.closest("td, th");
    if (!cell) return;
    const [row, column] = cursor;
    const inside = target !== cell;
    if (inside) {
      if (event.key === "Escape") {
        event.preventDefault();
        setControls(cell, false);
        (cell as HTMLElement).focus();
      }
      return;
    }
    const rtl = getComputedStyle(event.currentTarget).direction === "rtl";
    const forward = rtl ? "ArrowLeft" : "ArrowRight";
    const back = rtl ? "ArrowRight" : "ArrowLeft";
    const last = (grid.current?.rows.length ?? 1) - 1;
    if (event.key === "ArrowDown") move(row + 1, column);
    else if (event.key === "ArrowUp") move(row - 1, column);
    else if (event.key === forward) move(row, column + 1);
    else if (event.key === back) move(row, column - 1);
    else if (event.key === "Home") move(event.ctrlKey ? 0 : row, 0);
    else if (event.key === "End") move(event.ctrlKey ? last : row, columns.length - 1);
    else if (event.key === "Enter" || event.key === "F2") {
      const control = cell.querySelector<HTMLElement>(focusable);
      if (!control) return;
      setControls(cell, true);
      control.focus();
    } else return;
    event.preventDefault();
  }

  function onFocus(event: FocusEvent<HTMLTableElement>) {
    const cell = (event.target as HTMLElement).closest<HTMLTableCellElement>("td, th");
    const row = cell?.parentElement as HTMLTableRowElement | undefined;
    if (cell && row) setCursor([row.rowIndex, cell.cellIndex]);
  }

  function onBlur(event: FocusEvent<HTMLTableElement>) {
    const from = (event.target as HTMLElement).closest("td, th");
    const to = (event.relatedTarget as HTMLElement | null)?.closest("td, th");
    if (from && from !== to && event.target !== from) setControls(from, false);
  }

  const tab = (r: number, c: number) => (cursor[0] === r && cursor[1] === c ? 0 : -1);

  return (
    <div className="ui-table ui-grid">
      <div className="ui-table-scroll">
        <table ref={grid} role="grid" aria-labelledby={`${id}-caption`} onKeyDown={onKeyDown} onFocus={onFocus} onBlur={onBlur}>
          <caption id={`${id}-caption`}>{caption}</caption>
          <thead>
            <tr>
              {columns.map((c, i) => (
                <th key={c.key} role="columnheader" scope="col" tabIndex={tab(0, i)} data-align={c.align === "end" ? "end" : undefined}>
                  {c.header}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row, r) => (
              <tr key={rowKey(row)}>
                {columns.map((c, i) =>
                  c.rowHeader ? (
                    <th key={c.key} role="rowheader" scope="row" tabIndex={tab(r + 1, i)} data-align={c.align === "end" ? "end" : undefined}>
                      {c.cell(row)}
                    </th>
                  ) : (
                    <td key={c.key} role="gridcell" tabIndex={tab(r + 1, i)} data-align={c.align === "end" ? "end" : undefined}>
                      {c.cell(row)}
                    </td>
                  ),
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
