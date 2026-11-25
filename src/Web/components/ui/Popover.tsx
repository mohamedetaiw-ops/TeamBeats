"use client";

import { useEffect, useId, useRef, type ReactNode } from "react";
import { Button, type ButtonSize, type ButtonVariant } from "./Button";
import { Icon, type IconName } from "./Icon";
import { usePopover, type Placement } from "./overlay";

/**
 * Popover (spec 036 DLS-037): a small panel beside the button that opens it, for a few settings or details that do not
 * need a dialog. It uses the Popover API: the browser puts it above the page, closes it on Escape or a click outside
 * and returns focus to the button; the button says whether the panel is open. The page stays usable, and opening one
 * popover closes any other.
 *
 * The panel frame (DLS-045 to DLS-047) is the shell bar's: a fixed head with the title as a heading and an optional
 * action, a body that alone scrolls, a fixed footer, and a bottom sheet under 40rem. An icon only trigger keeps the
 * label as its accessible name and can carry a count, such as unread notifications.
 */
export function Popover({
  label,
  title,
  icon,
  iconOnly = false,
  badge,
  variant = "secondary",
  size = "md",
  placement = "bottom-start",
  frame = "plain",
  width = "md",
  list = false,
  headAction,
  footer,
  headBelow,
  onOpenChange,
  testId,
  triggerTestId,
  children,
}: {
  /** The text of the button that opens the panel; with `iconOnly` it is the button's accessible name. */
  label: string;
  /** A heading inside the panel. Without it the panel is named by the button. */
  title?: string;
  icon?: IconName;
  iconOnly?: boolean;
  /** A short count on the trigger, such as "3" or "99+". It is not read out, so the label says it in words. */
  badge?: string | null;
  variant?: ButtonVariant;
  size?: ButtonSize;
  placement?: Exclude<Placement, "side">;
  frame?: "plain" | "panel";
  width?: "md" | "wide";
  /** The body is a list of rows, which bring their own padding. */
  list?: boolean;
  /** In the panel head, after the title, such as "Mark all read". */
  headAction?: ReactNode;
  /** In the fixed footer of the panel frame, such as a link to the full page. */
  footer?: ReactNode;
  /** A row under the head's title, such as tabs that choose what the body shows. */
  headBelow?: ReactNode;
  /** Told when the panel opens or closes, so a feature can load its content only while it is open. */
  onOpenChange?: (open: boolean) => void;
  testId?: string;
  triggerTestId?: string;
  children: ReactNode;
}) {
  const id = useId();
  const titleId = useId();
  const triggerId = useId();
  const panelFrame = frame === "panel";
  const { trigger, panel, open, onPointerDown, onClick } = usePopover(placement, undefined, panelFrame);
  // Only a change of state is reported, not a new callback from the feature's render.
  const report = useRef(onOpenChange);
  useEffect(() => {
    report.current = onOpenChange;
  });
  useEffect(() => {
    report.current?.(open);
  }, [open]);
  const triggerProps = { id: triggerId, "aria-expanded": open, "aria-controls": id, "data-testid": triggerTestId, onPointerDown, onClick } as const;
  return (
    <>
      {iconOnly && icon ? (
        <button ref={trigger} type="button" className="button button-quiet ui-icon-button ui-popover-trigger" data-variant="quiet" title={label} {...triggerProps}>
          <Icon name={icon} size={20} />
          <span className="visually-hidden">{label}</span>
          {badge ? (
            <span className="ui-popover-badge" aria-hidden="true">
              {badge}
            </span>
          ) : null}
        </button>
      ) : (
        <Button ref={trigger} variant={variant} size={size} icon={icon} iconEnd="chevron-down" {...triggerProps}>
          {label}
        </Button>
      )}
      <div
        ref={panel}
        id={id}
        popover="auto"
        role="dialog"
        aria-labelledby={title ? titleId : triggerId}
        className="ui-popover"
        data-frame={panelFrame ? "panel" : undefined}
        data-width={width === "wide" ? "wide" : undefined}
        data-testid={testId}
      >
        {panelFrame ? (
          <>
            {title || headAction || headBelow ? (
              <div className="ui-popover-head">
                {title ? (
                  <h2 id={titleId} className="ui-popover-title">
                    {title}
                  </h2>
                ) : null}
                {headAction}
                {headBelow ? <div className="ui-popover-head-below">{headBelow}</div> : null}
              </div>
            ) : null}
            <div className="ui-popover-body" data-list={list || undefined}>
              {children}
            </div>
            {footer ? <div className="ui-popover-foot">{footer}</div> : null}
          </>
        ) : (
          <>
            {title ? (
              <p id={titleId} className="ui-popover-title">
                {title}
              </p>
            ) : null}
            {children}
          </>
        )}
      </div>
    </>
  );
}
