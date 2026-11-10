"use client";

import { useCallback, useEffect, useRef, useState } from "react";

/**
 * Shared behavior for parts that open above the page (spec 036 DLS-032, DLS-037): where a panel sits beside what
 * opened it, and how focus moves through a list of items. The platform gives the rest: the top layer, light dismiss,
 * Escape and focus return come from the Popover API and the dialog element.
 */
export type Placement = "bottom-start" | "bottom-end" | "top-start" | "top-end" | "side";

type Box = { top: number; bottom: number; left: number; right: number };

const edge = 8;
const gap = 4;

/**
 * Puts a top layer panel beside an anchor box, in logical coordinates so it mirrors in right-to-left layouts. A panel
 * that would leave the viewport flips to the other side of the anchor and is then kept inside the viewport.
 */
export function place(panel: HTMLElement, anchor: Box, placement: Placement) {
  const rtl = getComputedStyle(panel).direction === "rtl";
  const viewInline = document.documentElement.clientWidth;
  const viewBlock = window.innerHeight;
  const inlineSize = panel.offsetWidth;
  const blockSize = panel.offsetHeight;
  // Distances from the inline-start edge of the viewport to the two edges of the anchor.
  const anchorStart = rtl ? viewInline - anchor.right : anchor.left;
  const anchorEnd = rtl ? viewInline - anchor.left : anchor.right;

  let inline: number;
  let block: number;
  if (placement === "side") {
    inline = anchorEnd + gap;
    if (inline + inlineSize > viewInline - edge && anchorStart - gap - inlineSize >= edge) inline = anchorStart - gap - inlineSize;
    block = anchor.top;
    if (block + blockSize > viewBlock - edge) block = viewBlock - edge - blockSize;
  } else {
    inline = placement.endsWith("start") ? anchorStart : anchorEnd - inlineSize;
    const below = anchor.bottom + gap;
    const above = anchor.top - gap - blockSize;
    if (placement.startsWith("bottom")) block = below + blockSize > viewBlock - edge && above >= edge ? above : below;
    else block = above < edge && below + blockSize <= viewBlock - edge ? below : above;
  }
  inline = Math.max(edge, Math.min(inline, viewInline - inlineSize - edge));
  block = Math.max(edge, block);
  panel.style.insetInlineStart = `${Math.round(inline)}px`;
  panel.style.insetBlockStart = `${Math.round(block)}px`;
  panel.dataset.placed = "true";
}

/**
 * A button that opens a popover beside itself (Popover API). Opening places the panel and runs `onOpen` in the same
 * task as the click, so focus can move into the panel at once. The browser gives the top layer, Escape, a click
 * outside and focus return; this hook adds the place, the expanded state and closing by a second press of the button.
 */
export function usePopover(placement: Placement, onOpen?: (panel: HTMLElement) => void) {
  const trigger = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  // A press on the button while the panel is open first closes it as a click outside; the click must not reopen it.
  const pressedWhileOpen = useRef(false);

  const show = useCallback(() => {
    const el = panel.current;
    if (!el || !trigger.current) return;
    if (!el.matches(":popover-open")) el.showPopover();
    place(el, trigger.current.getBoundingClientRect(), placement);
    onOpen?.(el);
  }, [placement, onOpen]);

  const hide = useCallback(() => {
    if (panel.current?.matches(":popover-open")) panel.current.hidePopover();
    trigger.current?.focus();
  }, []);

  useEffect(() => {
    const el = panel.current;
    if (!el) return;
    const update = () => {
      if (trigger.current && el.matches(":popover-open")) place(el, trigger.current.getBoundingClientRect(), placement);
    };
    const onToggle = (event: Event) => {
      const opened = (event as ToggleEvent).newState === "open";
      setOpen(opened);
      if (!opened) delete el.dataset.placed;
    };
    el.addEventListener("toggle", onToggle);
    window.addEventListener("resize", update);
    window.addEventListener("scroll", update, true);
    return () => {
      el.removeEventListener("toggle", onToggle);
      window.removeEventListener("resize", update);
      window.removeEventListener("scroll", update, true);
    };
  }, [placement]);

  const onPointerDown = useCallback(() => {
    pressedWhileOpen.current = panel.current?.matches(":popover-open") ?? false;
  }, []);
  const onClick = useCallback(() => {
    const wasOpen = pressedWhileOpen.current || (panel.current?.matches(":popover-open") ?? false);
    pressedWhileOpen.current = false;
    if (wasOpen) hide();
    else show();
  }, [hide, show]);

  return { trigger, panel, open, show, hide, onPointerDown, onClick };
}

/** The next item for an arrow, Home or End key in a list that wraps; `null` when the key does not move focus. */
export function nextByKey<T>(items: readonly T[], current: number, key: string): T | null {
  if (items.length === 0) return null;
  switch (key) {
    case "ArrowDown":
      return items[(current + 1) % items.length]!;
    case "ArrowUp":
      return items[(current - 1 + items.length) % items.length]!;
    case "Home":
      return items[0]!;
    case "End":
      return items[items.length - 1]!;
    default:
      return null;
  }
}

/** The next item after `current` whose text starts with the typed character, for type ahead in menus and lists. */
export function nextByCharacter<T extends HTMLElement>(items: readonly T[], current: number, character: string): T | null {
  const wanted = character.toLocaleLowerCase();
  const ordered = [...items.slice(current + 1), ...items.slice(0, current + 1)];
  return ordered.find((item) => (item.textContent ?? "").trim().toLocaleLowerCase().startsWith(wanted)) ?? null;
}
