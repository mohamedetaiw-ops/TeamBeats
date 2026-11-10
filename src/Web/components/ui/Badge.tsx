import type { ReactNode } from "react";
import { Icon, type IconName } from "./Icon";

export type BadgeTone = "neutral" | "accent" | "info" | "success" | "warning" | "danger";

/** Short status or category label. Meaning is never carried by color alone: the text says it, the icon helps. */
export function Badge({ tone = "neutral", icon, children }: { tone?: BadgeTone; icon?: IconName; children: ReactNode }) {
  return (
    <span className="ui-badge" data-tone={tone}>
      {icon ? <Icon name={icon} size={14} /> : null}
      <span>{children}</span>
    </span>
  );
}
