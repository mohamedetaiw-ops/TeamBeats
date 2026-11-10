"use client";

import { useCallback, useEffect, useId, useState, type RefObject } from "react";

/** The one event shell panels share: the id of the panel that just opened. */
const opened = "tb:shell-panel-opened";

/**
 * One shell panel at a time (spec 008 FR-060): opening a panel in the bar (settings, notifications, account) closes
 * any other, and a press outside the panel's root closes it. Escape stays with each panel, which also returns focus
 * to its button. Panels never import each other; they share only this event.
 */
export function useShellPanel(root: RefObject<HTMLElement | null>): [boolean, (open: boolean) => void] {
  const id = useId();
  const [open, setOpenState] = useState(false);

  const setOpen = useCallback(
    (next: boolean) => {
      setOpenState(next);
      if (next) window.dispatchEvent(new CustomEvent<string>(opened, { detail: id }));
    },
    [id],
  );

  useEffect(() => {
    const other = (e: Event) => {
      if ((e as CustomEvent<string>).detail !== id) setOpenState(false);
    };
    const outside = (e: PointerEvent) => {
      if (root.current && !root.current.contains(e.target as Node)) setOpenState(false);
    };
    window.addEventListener(opened, other);
    document.addEventListener("pointerdown", outside);
    return () => {
      window.removeEventListener(opened, other);
      document.removeEventListener("pointerdown", outside);
    };
  }, [id, root]);

  return [open, setOpen];
}
