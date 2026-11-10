"use client";

import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { useRouter } from "next/navigation";
import { Icon, type IconName } from "./Icon";

export type Command = {
  key: string;
  label: string;
  /** One line under the label, such as where a page lives. */
  hint?: string;
  icon?: IconName;
  shortcut?: readonly string[];
  /** Extra words the command is found by. */
  keywords?: string;
  href?: string;
  onSelect?: () => void;
};
export type CommandGroup = { key: string; label: string; commands: readonly Command[] };

/**
 * Command palette (spec 036 DLS-036): a search box over a list of pages and actions, opened with a shortcut. It is a
 * modal dialog holding a combobox: focus stays in the box, the arrow keys move the highlighted command, Enter runs it
 * and Escape closes. By default it filters the commands it was given; a caller that searches on the server passes
 * `onQueryChange` and gives back the groups to show.
 */
export function CommandPalette({
  open,
  onClose,
  label,
  placeholder,
  empty,
  groups,
  onQueryChange,
}: {
  open: boolean;
  onClose: () => void;
  /** The name of the dialog and of the list, such as "Go to or search". */
  label: string;
  placeholder: string;
  /** Shown when nothing matches. */
  empty: string;
  groups: readonly CommandGroup[];
  onQueryChange?: (query: string) => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const input = useRef<HTMLInputElement>(null);
  const listId = useId();
  const router = useRouter();
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);

  useEffect(() => {
    const el = dialog.current;
    if (!el) return;
    if (open && !el.open) el.showModal();
    if (!open && el.open) el.close();
  }, [open]);

  const shown = useMemo(() => {
    const term = query.trim().toLocaleLowerCase();
    if (!term || onQueryChange) return groups;
    return groups
      .map((group) => ({
        ...group,
        commands: group.commands.filter((c) => `${c.label} ${c.hint ?? ""} ${c.keywords ?? ""}`.toLocaleLowerCase().includes(term)),
      }))
      .filter((group) => group.commands.length > 0);
  }, [groups, query, onQueryChange]);
  const flat = shown.flatMap((group) => group.commands);
  const current = Math.min(active, Math.max(flat.length - 1, 0));
  const optionId = (index: number) => `${listId}-option-${index}`;

  function change(next: string) {
    setQuery(next);
    setActive(0);
    onQueryChange?.(next);
  }

  function run(command: Command | undefined) {
    if (!command) return;
    dialog.current?.close();
    if (command.href) router.push(command.href);
    else command.onSelect?.();
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    const last = flat.length - 1;
    let next: number | null = null;
    if (event.key === "ArrowDown") next = current >= last ? 0 : current + 1;
    else if (event.key === "ArrowUp") next = current <= 0 ? last : current - 1;
    else if (event.key === "Home" && flat.length > 0 && query === "") next = 0;
    else if (event.key === "End" && flat.length > 0 && query === "") next = last;
    else if (event.key === "Enter") {
      event.preventDefault();
      run(flat[current]);
      return;
    }
    if (next === null || last < 0) return;
    event.preventDefault();
    setActive(next);
    document.getElementById(optionId(next))?.scrollIntoView({ block: "nearest" });
  }

  // The position of each group's first command in the flat list that the arrow keys walk.
  const starts = shown.map((_, g) => shown.slice(0, g).reduce((count, group) => count + group.commands.length, 0));
  return (
    <dialog
      ref={dialog}
      className="ui-dialog ui-palette"
      aria-label={label}
      onClose={() => {
        change("");
        onClose();
      }}
      onClick={(event) => {
        if (event.target === dialog.current) dialog.current?.close();
      }}
    >
      <div className="ui-dialog-panel">
        <div className="ui-palette-search">
          <Icon name="search" size={18} />
          <input
            ref={input}
            type="text"
            role="combobox"
            aria-label={placeholder}
            aria-expanded="true"
            aria-controls={listId}
            aria-autocomplete="list"
            aria-activedescendant={flat.length > 0 ? optionId(current) : undefined}
            placeholder={placeholder}
            autoComplete="off"
            spellCheck={false}
            value={query}
            onChange={(event) => change(event.currentTarget.value)}
            onKeyDown={onKeyDown}
          />
        </div>
        <div id={listId} role="listbox" aria-label={label} className="ui-palette-list">
          {shown.map((group, g) => (
            <div key={group.key} role="group" aria-label={group.label} className="ui-palette-group">
              <span className="ui-menu-group-label" aria-hidden="true">
                {group.label}
              </span>
              {group.commands.map((command, c) => {
                const mine = starts[g]! + c;
                return (
                  <div
                    key={command.key}
                    id={optionId(mine)}
                    role="option"
                    aria-selected={mine === current}
                    className="ui-palette-option"
                    onPointerMove={() => setActive(mine)}
                    onClick={() => run(command)}
                  >
                    <span className="ui-menu-mark" aria-hidden="true">
                      {command.icon ? <Icon name={command.icon} size={16} /> : null}
                    </span>
                    <span className="ui-palette-text">
                      <span className="ui-menu-label">{command.label}</span>
                      {command.hint ? <span className="ui-palette-hint">{command.hint}</span> : null}
                    </span>
                    {command.shortcut ? (
                      <span className="ui-menu-shortcut" aria-hidden="true">
                        {command.shortcut.map((key) => (
                          <kbd key={key} className="ui-kbd">
                            {key}
                          </kbd>
                        ))}
                      </span>
                    ) : null}
                  </div>
                );
              })}
            </div>
          ))}
        </div>
        <p role="status" className="ui-palette-empty">
          {flat.length === 0 ? empty : null}
        </p>
      </div>
    </dialog>
  );
}
