"use client";

import { cloneElement, useId, useState, type ReactElement } from "react";

/**
 * Tooltip (spec 036 DLS-037): one short line that adds to a control's name, such as a shortcut or what a setting
 * affects. It shows on hover and on keyboard focus, stays while the pointer is on it, and Escape hides it without
 * moving focus (WCAG 2.2 SC 1.4.13). It is tied to the control with `aria-describedby`, so it is read after the name.
 * Never put the only name of a control, a link or anything to click in a tooltip.
 */
export function Tooltip({
  text,
  placement = "top",
  children,
}: {
  text: string;
  placement?: "top" | "bottom";
  /** One focusable control. */
  children: ReactElement<{ "aria-describedby"?: string }>;
}) {
  const id = useId();
  const [dismissed, setDismissed] = useState(false);
  return (
    <span
      className="ui-tooltip-anchor"
      data-dismissed={dismissed || undefined}
      onKeyDown={(event) => {
        if (event.key === "Escape") setDismissed(true);
      }}
      onPointerLeave={() => setDismissed(false)}
      onBlur={() => setDismissed(false)}
    >
      {cloneElement(children, { "aria-describedby": id })}
      <span role="tooltip" id={id} className="ui-tooltip" data-placement={placement}>
        {text}
      </span>
    </span>
  );
}
