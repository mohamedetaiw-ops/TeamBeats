"use client";

import { useCallback, useEffect, useId, useRef, useState, type KeyboardEvent, type PointerEvent, type ReactNode } from "react";
import Link from "next/link";
import type { ButtonSize, ButtonVariant } from "./Button";
import { Icon, type IconName } from "./Icon";
import { nextByCharacter, nextByKey, place, usePopover, type Placement } from "./overlay";

export type MenuAction = {
  kind?: "item";
  key: string;
  label: string;
  icon?: IconName;
  /** Key names shown at the end of the row, such as ["Ctrl", "D"]. The feature binds the shortcut; the menu only shows it. */
  shortcut?: readonly string[];
  /** A link item goes to a page; it opens in a new tab like any link. */
  href?: string;
  onSelect?: () => void;
  disabled?: boolean;
  /** A destructive action looks different and sits last in its group. */
  danger?: boolean;
  /** Makes the item a checkbox or, with `radio`, one choice of its group. */
  checked?: boolean;
  radio?: boolean;
  /** A submenu. Keep to one level; deeper trees belong on a page. */
  items?: readonly MenuEntry[];
};
export type MenuEntry = MenuAction | { kind: "separator"; key: string } | { kind: "group"; key: string; label: string; items: readonly MenuAction[] };

const itemSelector = '[role="menuitem"], [role="menuitemcheckbox"], [role="menuitemradio"]';

/** The items of one menu, without those of its submenus. */
function itemsOf(menu: HTMLElement): HTMLElement[] {
  return [...menu.querySelectorAll<HTMLElement>(itemSelector)].filter((item) => item.closest('[role="menu"]') === menu);
}

function focusFirst(menu: HTMLElement | null | undefined) {
  if (menu) itemsOf(menu)[0]?.focus();
}

/** Moves focus to the first item of the menu inside a panel that just opened. */
function focusMenu(panel: HTMLElement) {
  focusFirst(panel.querySelector<HTMLElement>('[role="menu"]'));
}

/**
 * The list of a menu (WAI-ARIA menu pattern): arrow keys move and wrap, Home and End jump, a letter jumps to the next
 * item that starts with it, the forward arrow opens a submenu and the back arrow leaves it. Disabled items can be
 * focused, so people learn that they exist, but do nothing. Choosing an item runs it and closes the whole menu.
 */
export function MenuList({ label, items, onDone }: { label: string; items: readonly MenuEntry[]; onDone: () => void }) {
  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const menu = event.currentTarget;
    const target = event.target as HTMLElement;
    // Keys pressed in a submenu are handled by that submenu.
    if (target.closest('[role="menu"]') !== menu) return;
    const all = itemsOf(menu);
    const current = all.indexOf(target);
    const rtl = getComputedStyle(menu).direction === "rtl";
    const forward = rtl ? "ArrowLeft" : "ArrowRight";
    const back = rtl ? "ArrowRight" : "ArrowLeft";

    const next = nextByKey(all, current, event.key);
    if (next) {
      event.preventDefault();
      next.focus();
    } else if (event.key === forward && target.dataset.submenu) {
      event.preventDefault();
      openSubmenu(target);
    } else if (event.key === back) {
      const parent = menu.parentElement?.closest<HTMLElement>("[popover]");
      const opener = parent?.previousElementSibling as HTMLElement | null;
      if (parent && opener?.dataset.submenu) {
        event.preventDefault();
        parent.hidePopover();
        opener.focus();
      }
    } else if (event.key === "Tab") {
      onDone();
    } else if (event.key.length === 1 && event.key !== " " && !event.ctrlKey && !event.metaKey && !event.altKey) {
      nextByCharacter(all, current, event.key)?.focus();
    }
  }

  function onPointerMove(event: PointerEvent<HTMLDivElement>) {
    const menu = event.currentTarget;
    const item = (event.target as HTMLElement).closest<HTMLElement>(itemSelector);
    if (!item || item.closest('[role="menu"]') !== menu || item === document.activeElement) return;
    item.focus();
    // Pointing at another row closes a submenu that was open; pointing at a submenu row opens it.
    for (const open of menu.querySelectorAll<HTMLElement>(":scope > .ui-menu-sub:popover-open, :scope > [role=group] > .ui-menu-sub:popover-open")) {
      if (open.previousElementSibling !== item) open.hidePopover();
    }
    if (item.dataset.submenu) openSubmenu(item, false);
  }

  const row = (item: MenuAction) => <MenuRow key={item.key} item={item} onDone={onDone} />;
  return (
    <div role="menu" aria-label={label} className="ui-menu" onKeyDown={onKeyDown} onPointerMove={onPointerMove}>
      {items.map((entry) =>
        entry.kind === "separator" ? (
          <div key={entry.key} role="separator" className="ui-menu-separator" />
        ) : entry.kind === "group" ? (
          <div key={entry.key} role="group" aria-label={entry.label} className="ui-menu-group">
            <span className="ui-menu-group-label" aria-hidden="true">
              {entry.label}
            </span>
            {entry.items.map(row)}
          </div>
        ) : (
          row(entry)
        ),
      )}
    </div>
  );
}

function openSubmenu(opener: HTMLElement, moveFocus = true) {
  const panel = opener.nextElementSibling as HTMLElement | null;
  if (!panel?.matches("[popover]")) return;
  if (!panel.matches(":popover-open")) panel.showPopover();
  place(panel, opener.getBoundingClientRect(), "side");
  if (moveFocus) focusFirst(panel.querySelector<HTMLElement>('[role="menu"]'));
}

function MenuRow({ item, onDone }: { item: MenuAction; onDone: () => void }) {
  const role = item.checked === undefined ? "menuitem" : item.radio ? "menuitemradio" : "menuitemcheckbox";
  const content: ReactNode = (
    <>
      <span className="ui-menu-mark" aria-hidden="true">
        {item.checked ? <Icon name="check" size={16} /> : item.icon ? <Icon name={item.icon} size={16} /> : null}
      </span>
      <span className="ui-menu-label">{item.label}</span>
      {item.shortcut ? (
        <span className="ui-menu-shortcut" aria-hidden="true">
          {item.shortcut.map((key) => (
            <kbd key={key} className="ui-kbd">
              {key}
            </kbd>
          ))}
        </span>
      ) : null}
      {item.items ? <Icon name="chevron-forward" size={16} /> : null}
    </>
  );
  const shared = {
    role,
    tabIndex: -1,
    className: "ui-menu-item",
    "data-danger": item.danger || undefined,
    "aria-disabled": item.disabled || undefined,
    "aria-checked": item.checked,
    "aria-keyshortcuts": item.shortcut?.join("+"),
  } as const;

  if (item.items) {
    return (
      <>
        <button
          type="button"
          {...shared}
          data-submenu="true"
          aria-haspopup="menu"
          onClick={(event) => {
            if (!item.disabled) openSubmenu(event.currentTarget);
          }}
        >
          {content}
        </button>
        <div popover="auto" className="ui-menu-popover ui-menu-sub">
          <MenuList label={item.label} items={item.items} onDone={onDone} />
        </div>
      </>
    );
  }
  if (item.href && !item.disabled) {
    return (
      <Link href={item.href} {...shared} onClick={onDone}>
        {content}
      </Link>
    );
  }
  return (
    <button
      type="button"
      {...shared}
      onClick={() => {
        if (item.disabled) return;
        item.onSelect?.();
        onDone();
      }}
    >
      {content}
    </button>
  );
}

/**
 * Menu (spec 036 DLS-036): a button that opens a list of actions on the thing beside it. The button says what the
 * menu is for; with `iconOnly` it shows just the icon and `label` is its accessible name. Opening moves focus to the
 * first item, and closing returns it to the button.
 */
export function Menu({
  label,
  items,
  icon,
  iconOnly = false,
  variant = "secondary",
  size = "md",
  placement = "bottom-start",
}: {
  label: string;
  items: readonly MenuEntry[];
  icon?: IconName;
  iconOnly?: boolean;
  variant?: ButtonVariant;
  size?: ButtonSize;
  placement?: Exclude<Placement, "side">;
}) {
  const id = useId();
  const { trigger, panel, open, show, hide, onPointerDown, onClick } = usePopover(placement, focusMenu);
  return (
    <>
      <button
        ref={trigger}
        type="button"
        className={["button", `button-${variant}`, iconOnly ? "ui-icon-button" : null].filter(Boolean).join(" ")}
        data-variant={variant}
        data-size={size === "md" ? undefined : size}
        data-icon={!iconOnly || undefined}
        aria-haspopup="menu"
        aria-label={iconOnly ? label : undefined}
        title={iconOnly ? label : undefined}
        aria-expanded={open}
        aria-controls={id}
        onPointerDown={onPointerDown}
        onClick={onClick}
        onKeyDown={(event) => {
          if (event.key === "ArrowDown") {
            event.preventDefault();
            show();
          }
        }}
      >
        {iconOnly ? (
          <Icon name={icon ?? "more"} size={size === "xs" ? 14 : 18} />
        ) : (
          <>
            {icon ? <Icon name={icon} size={16} /> : null}
            <span>{label}</span>
            <Icon name="chevron-down" size={16} />
          </>
        )}
      </button>
      <div ref={panel} id={id} popover="auto" className="ui-menu-popover">
        <MenuList label={label} items={items} onDone={hide} />
      </div>
    </>
  );
}

/**
 * Context menu (spec 036 DLS-036): the same list, opened on the thing itself with the secondary mouse button, a long
 * press, or the keyboard's menu key or Shift+F10. It is a shortcut, never the only way: every action in it must also
 * be reachable from a visible control, such as a Menu on the row.
 */
export function ContextMenu({ label, items, className, children }: { label: string; items: readonly MenuEntry[]; className?: string; children: ReactNode }) {
  const panel = useRef<HTMLDivElement>(null);
  const opener = useRef<HTMLElement | null>(null);
  const [open, setOpen] = useState(false);
  const close = useCallback(() => {
    if (panel.current?.matches(":popover-open")) panel.current.hidePopover();
    setOpen(false);
    opener.current?.focus();
  }, []);

  // The panel is a manual popover: the press that opens it would close an automatic one when the button is released.
  // So closing on Escape, on a press outside and when the page scrolls is done here.
  useEffect(() => {
    if (!open) return;
    const onKey = (event: globalThis.KeyboardEvent) => {
      // Escape inside an open submenu closes only the submenu, which the browser does.
      if (event.key === "Escape" && !panel.current?.querySelector(".ui-menu-sub:popover-open")) close();
    };
    const onPress = (event: globalThis.PointerEvent) => {
      if (!panel.current?.contains(event.target as Node)) close();
    };
    document.addEventListener("keydown", onKey);
    document.addEventListener("pointerdown", onPress);
    window.addEventListener("scroll", close, true);
    window.addEventListener("resize", close);
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("pointerdown", onPress);
      window.removeEventListener("scroll", close, true);
      window.removeEventListener("resize", close);
    };
  }, [open, close]);

  return (
    <div
      className={className}
      onContextMenu={(event) => {
        const el = panel.current;
        if (!el || el.contains(event.target as Node)) return;
        event.preventDefault();
        opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
        if (!el.matches(":popover-open")) el.showPopover();
        place(el, { top: event.clientY, bottom: event.clientY, left: event.clientX, right: event.clientX }, "bottom-start");
        focusMenu(el);
        setOpen(true);
      }}
    >
      {children}
      <div ref={panel} popover="manual" className="ui-menu-popover">
        <MenuList label={label} items={items} onDone={close} />
      </div>
    </div>
  );
}
