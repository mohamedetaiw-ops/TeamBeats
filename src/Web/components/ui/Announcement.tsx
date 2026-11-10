"use client";

import { useState, type ReactNode } from "react";
import { CloseButton } from "./IconButton";
import { Icon, type IconName } from "./Icon";

/**
 * Announcement (spec 036 DLS-037): a banner for news about the product or workplace, such as a new feature or a
 * planned maintenance window. It carries one short title, one line and at most one action. It is not for errors or
 * the result of an action (use Alert) and not for urgent safety notices (the shell security banner).
 * Dismissing hides it here; the caller remembers the dismissal through `onDismiss` so it stays hidden.
 */
export function Announcement({
  title,
  children,
  icon = "megaphone",
  action,
  onDismiss,
  dismissLabel,
}: {
  title: string;
  children?: ReactNode;
  icon?: IconName;
  action?: ReactNode;
  onDismiss?: () => void;
  dismissLabel?: string;
}) {
  const [open, setOpen] = useState(true);
  if (!open) return null;
  return (
    <aside className="ui-announcement" aria-label={title}>
      <span className="ui-announcement-icon" aria-hidden="true">
        <Icon name={icon} size={18} />
      </span>
      <div className="ui-announcement-text">
        <p className="ui-announcement-title">{title}</p>
        {children ? <p>{children}</p> : null}
      </div>
      {action ? <div className="ui-announcement-action">{action}</div> : null}
      {onDismiss ? (
        <CloseButton
          label={dismissLabel}
          onClick={() => {
            setOpen(false);
            onDismiss();
          }}
        />
      ) : null}
    </aside>
  );
}
