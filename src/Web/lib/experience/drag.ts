"use client";

import { useRef, type PointerEvent } from "react";

/** How the items and areas of one editor are marked in the page: CSS selectors and the data attributes holding ids. */
export type DragTargets = { item: string; itemKey: string; region: string; regionKey: string };

export type Drop = { regionId: string; position: number; target: Element; after: boolean };

/**
 * Where a dragged item would land under the pointer (spec 008 FR-024, spec 032 FR-038): before or after the item it is
 * over, or at the end of an area when it is over the area but no item. Only areas in `allowed` take it. The position
 * counts the items of that area without the dragged one, and reading direction decides which side is "after".
 */
export function dropAt(x: number, y: number, dragged: string, allowed: ReadonlySet<string>, targets: DragTargets): Drop | null {
  for (const element of document.elementsFromPoint(x, y)) {
    const region = element.closest<HTMLElement>(targets.region);
    const regionId = region?.dataset[targets.regionKey];
    if (!region || !regionId || !allowed.has(regionId)) continue;
    const item = element.closest<HTMLElement>(targets.item);
    const items = [...region.querySelectorAll<HTMLElement>(targets.item)].filter((e) => e.dataset[targets.itemKey] !== dragged);
    if (!item) return { regionId, position: items.length, target: region, after: true };
    if (item.dataset[targets.itemKey] === dragged) return null;
    const box = item.getBoundingClientRect();
    const across = box.width < region.getBoundingClientRect().width * 0.9;
    const rtl = getComputedStyle(item).direction === "rtl";
    const middle = box.left + box.width / 2;
    const after = across ? (rtl ? x < middle : x > middle) : y > box.top + box.height / 2;
    return { regionId, position: items.indexOf(item) + (after ? 1 : 0), target: item, after };
  }
  return null;
}

/**
 * Pointer handlers for a drag handle: mouse, pen and touch all work, because the handle captures the pointer. While
 * dragging, the item carries `data-dragging` and the place it would land carries `data-drop` (before, after or inside),
 * so each editor draws its own marks. Every editor keeps buttons for the same moves, for the keyboard.
 */
export function useDragHandle({
  id,
  allowed,
  targets,
  disabled,
  onDrop,
}: {
  id: string;
  allowed: ReadonlySet<string>;
  targets: DragTargets;
  disabled?: boolean;
  onDrop: (drop: Drop) => void;
}) {
  const drop = useRef<Drop | null>(null);
  const item = useRef<Element | null>(null);

  function mark(next: Drop | null) {
    drop.current?.target.removeAttribute("data-drop");
    drop.current = next;
    next?.target.setAttribute("data-drop", next.target.matches(targets.region) && !next.target.matches(targets.item) ? "inside" : next.after ? "after" : "before");
  }

  function end(e: PointerEvent<HTMLElement>) {
    item.current?.removeAttribute("data-dragging");
    item.current = null;
    const target = drop.current;
    mark(null);
    if (target && e.type === "pointerup") onDrop(target);
  }

  return {
    onPointerDown(e: PointerEvent<HTMLElement>) {
      if (disabled || e.button !== 0) return;
      // Keeps the browser from selecting text or scrolling while the pointer moves.
      e.preventDefault();
      e.currentTarget.setPointerCapture(e.pointerId);
      item.current = e.currentTarget.closest(targets.item);
      item.current?.setAttribute("data-dragging", "");
    },
    onPointerMove(e: PointerEvent<HTMLElement>) {
      if (item.current && e.currentTarget.hasPointerCapture(e.pointerId)) mark(dropAt(e.clientX, e.clientY, id, allowed, targets));
    },
    onPointerUp: end,
    onPointerCancel: end,
  };
}
