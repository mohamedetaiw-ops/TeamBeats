"use client";

import { useEffect, useRef, useState, type ReactNode } from "react";
import { Button } from "./Button";

/**
 * A destructive or high-impact action that needs a second, explicit step (FR-040): the first press only asks, so a
 * single accidental tap never runs it. Focus moves to the safe choice, and Escape cancels.
 */
export function ConfirmAction({
  label,
  question,
  confirmLabel,
  cancelLabel,
  onConfirm,
}: {
  label: string;
  question: ReactNode;
  confirmLabel: string;
  cancelLabel: string;
  onConfirm: () => void | Promise<void>;
}) {
  const [asking, setAsking] = useState(false);
  const [busy, setBusy] = useState(false);
  const cancel = useRef<HTMLButtonElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (asking) cancel.current?.focus();
  }, [asking]);

  function close() {
    setAsking(false);
    requestAnimationFrame(() => trigger.current?.focus());
  }

  async function confirm() {
    setBusy(true);
    try {
      await onConfirm();
    } finally {
      setBusy(false);
      setAsking(false);
    }
  }

  if (!asking) {
    return (
      <Button ref={trigger} variant="destructive" onClick={() => setAsking(true)} aria-haspopup="dialog">
        {label}
      </Button>
    );
  }
  return (
    <div
      className="ui-confirm"
      role="alertdialog"
      aria-modal="false"
      aria-label={label}
      onKeyDown={(e) => {
        if (e.key === "Escape") {
          // Escape closes only the question, not a menu or panel around it.
          e.stopPropagation();
          close();
        }
      }}
    >
      <p className="ui-confirm-question">{question}</p>
      <div className="ui-actions">
        <Button ref={cancel} variant="secondary" onClick={close}>
          {cancelLabel}
        </Button>
        <Button variant="destructive" busy={busy} onClick={() => void confirm()}>
          {confirmLabel}
        </Button>
      </div>
    </div>
  );
}
