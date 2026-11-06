import type { ReactNode } from "react";
import { Button } from "./Button";

/** Bulk selection (FR-048): the count and the consequence are stated in text before the action, and it can be cancelled. */
export function SelectionBar({
  summary,
  consequence,
  cancelLabel,
  onCancel,
  children,
}: {
  summary: string;
  consequence: string;
  cancelLabel: string;
  onCancel: () => void;
  children: ReactNode;
}) {
  return (
    <div className="ui-selection" role="region" aria-label={summary}>
      <p className="ui-selection-text">
        <strong>{summary}</strong> <span>{consequence}</span>
      </p>
      <div className="ui-actions">
        <Button variant="secondary" onClick={onCancel}>
          {cancelLabel}
        </Button>
        {children}
      </div>
    </div>
  );
}
