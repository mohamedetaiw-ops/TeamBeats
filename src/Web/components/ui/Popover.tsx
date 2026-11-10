"use client";

import { useId, type ReactNode } from "react";
import { Button, type ButtonSize, type ButtonVariant } from "./Button";
import type { IconName } from "./Icon";
import { usePopover, type Placement } from "./overlay";

/**
 * Popover (spec 036 DLS-037): a small panel beside the button that opens it, for a few settings or details that do not
 * need a dialog. It uses the Popover API: the browser puts it above the page, closes it on Escape or a click outside
 * and returns focus to the button; the button says whether the panel is open. The page stays usable.
 */
export function Popover({
  label,
  title,
  icon,
  variant = "secondary",
  size = "md",
  placement = "bottom-start",
  children,
}: {
  /** The text of the button that opens the panel. */
  label: string;
  /** A heading inside the panel. Without it the panel is named by the button. */
  title?: string;
  icon?: IconName;
  variant?: ButtonVariant;
  size?: ButtonSize;
  placement?: Exclude<Placement, "side">;
  children: ReactNode;
}) {
  const id = useId();
  const titleId = useId();
  const triggerId = useId();
  const { trigger, panel, open, onPointerDown, onClick } = usePopover(placement);
  return (
    <>
      <Button ref={trigger} id={triggerId} variant={variant} size={size} icon={icon} iconEnd="chevron-down" aria-expanded={open} aria-controls={id} onPointerDown={onPointerDown} onClick={onClick}>
        {label}
      </Button>
      <div ref={panel} id={id} popover="auto" role="dialog" aria-labelledby={title ? titleId : triggerId} className="ui-popover">
        {title ? (
          <p id={titleId} className="ui-popover-title">
            {title}
          </p>
        ) : null}
        {children}
      </div>
    </>
  );
}
