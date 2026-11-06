import type { ReactNode } from "react";
import { useTranslations } from "next-intl";

/** The shared states of spec 008 FR-006. Every component renders these instead of drawing its own. */
export type StateKind = "loading" | "success" | "empty" | "error" | "denied" | "offline" | "unsupported";

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
  ...data
}: {
  kind: StateKind;
  title?: string;
  message?: string;
  action?: ReactNode;
  /** Keeps the space the content would take, so the state never shifts the layout (FR-045). */
  minBlockSize?: string;
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
      <p className="ui-state-title">{title ?? t(`${kind}.title`)}</p>
      {message !== "" ? <p className="ui-state-message">{message ?? t(`${kind}.message`)}</p> : null}
      {action ? <div className="ui-state-action">{action}</div> : null}
    </div>
  );
}
