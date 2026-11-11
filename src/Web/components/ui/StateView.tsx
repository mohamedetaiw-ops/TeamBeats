import type { ReactNode } from "react";
import { useTranslations } from "next-intl";
import { Icon, type IconName } from "./Icon";

/** The shared states of spec 008 FR-006. Every component renders these instead of drawing its own. */
export type StateKind = "loading" | "success" | "empty" | "error" | "denied" | "offline" | "unsupported";

/** Empty, error and denied show an icon tile, a title and one line of help (DLS-052); loading shows none. */
const icons: Record<Exclude<StateKind, "loading">, IconName> = {
  success: "check",
  empty: "inbox",
  error: "alert",
  denied: "lock",
  offline: "globe",
  unsupported: "info",
};

/**
 * One shared state view. Title and message default to the platform wording, so equivalent states read the same in
 * every component pack (US1 scenario 2); a component can pass its own text and one action, such as retry.
 */
export function StateView({
  kind,
  title,
  message,
  action,
  minBlockSize,
  icon,
  ...data
}: {
  kind: StateKind;
  title?: string;
  message?: string;
  action?: ReactNode;
  /** Keeps the space the content would take, so the state never shifts the layout (FR-045). */
  minBlockSize?: string;
  /** A more specific icon for the tile, such as search for a filter that finds nothing. */
  icon?: IconName;
} & Record<`data-${string}`, string | undefined>) {
  const t = useTranslations("ui.state");
  const busy = kind === "loading";
  return (
    <div
      className="ui-state"
      data-state={kind}
      role={busy ? undefined : "status"}
      aria-busy={busy || undefined}
      style={minBlockSize ? { minBlockSize } : undefined}
      {...data}
    >
      {busy ? null : (
        <span className="ui-state-icon" aria-hidden="true">
          <Icon name={icon ?? icons[kind as Exclude<StateKind, "loading">]} size={20} />
        </span>
      )}
      <p className="ui-state-title">{title ?? t(`${kind}.title`)}</p>
      {message !== "" ? <p className="ui-state-message">{message ?? t(`${kind}.message`)}</p> : null}
      {action ? <div className="ui-state-action">{action}</div> : null}
    </div>
  );
}
