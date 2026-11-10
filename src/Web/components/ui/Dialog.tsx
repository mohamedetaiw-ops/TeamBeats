"use client";

import { useEffect, useId, useRef, type MouseEvent, type ReactNode } from "react";
import { CloseButton } from "./IconButton";

export type DialogSize = "sm" | "md" | "lg";
export type DrawerSide = "start" | "end" | "top" | "bottom";

type Props = {
  open: boolean;
  /** Called when the dialog closes for any reason: the close button, Escape, a click outside, or `open` turning false. */
  onClose: () => void;
  title: string;
  description?: string;
  children?: ReactNode;
  /** The actions, in reading order: the main action last. */
  footer?: ReactNode;
  size?: DialogSize;
  closeLabel?: string;
};

/**
 * Dialog (spec 036 DLS-037): a task or a question that must be finished or dismissed before going on. It is the native
 * dialog element opened as a modal, so the browser keeps focus inside it, makes the page behind inert, closes it on
 * Escape and returns focus to what opened it. A click on the backdrop closes it too. For a destructive confirmation
 * inside a page, prefer ConfirmAction; a dialog is for work that needs room.
 */
export function Dialog(props: Props) {
  return <Surface {...props} />;
}

/** Drawer (spec 036 DLS-037): the same modal behavior, attached to an edge. Use it for details and filters beside a list. */
export function Drawer({ side = "end", ...props }: Props & { side?: DrawerSide }) {
  return <Surface {...props} side={side} />;
}

function Surface({ open, onClose, title, description, children, footer, size = "md", closeLabel, side }: Props & { side?: DrawerSide }) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const descriptionId = useId();

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    if (open && !el.open) el.showModal();
    if (!open && el.open) el.close();
  }, [open]);

  function onBackdrop(event: MouseEvent<HTMLDialogElement>) {
    // The panel fills the dialog, so a click whose target is the dialog itself landed on the backdrop.
    if (event.target === ref.current) ref.current?.close();
  }

  return (
    <dialog
      ref={ref}
      className="ui-dialog"
      data-size={size}
      data-side={side}
      aria-labelledby={titleId}
      aria-describedby={description ? descriptionId : undefined}
      onClose={onClose}
      onClick={onBackdrop}
    >
      <div className="ui-dialog-panel">
        <header className="ui-dialog-head">
          <h2 id={titleId}>{title}</h2>
          <CloseButton label={closeLabel} onClick={() => ref.current?.close()} />
        </header>
        {description ? (
          <p id={descriptionId} className="ui-dialog-description">
            {description}
          </p>
        ) : null}
        {children ? <div className="ui-dialog-body">{children}</div> : null}
        {footer ? <footer className="ui-dialog-foot">{footer}</footer> : null}
      </div>
    </dialog>
  );
}
