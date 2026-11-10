import type { ReactNode } from "react";
import { Icon, type IconName } from "./Icon";

export type AlertTone = "info" | "success" | "warning" | "danger";

const icons: Record<AlertTone, IconName> = { info: "info", success: "check", warning: "alert", danger: "alert" };

/**
 * Inline alert inside a page or form (docs/design-system/components.md). Title says what happened, body says what to
 * do next. Errors and warnings that appear after an action are announced (`live`); alerts present on load are not.
 */
export function Alert({
  tone = "info",
  title,
  children,
  action,
  live = false,
}: {
  tone?: AlertTone;
  title: string;
  children?: ReactNode;
  action?: ReactNode;
  live?: boolean;
}) {
  return (
    <div className="ui-alert" data-tone={tone} role={live ? (tone === "danger" ? "alert" : "status") : undefined}>
      <Icon name={icons[tone]} size={20} />
      <div className="ui-alert-body">
        <p className="ui-alert-title">{title}</p>
        {children ? <div className="ui-alert-text">{children}</div> : null}
        {action ? <div className="ui-alert-action">{action}</div> : null}
      </div>
    </div>
  );
}
