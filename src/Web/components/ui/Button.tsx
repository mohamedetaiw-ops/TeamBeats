import type { ComponentPropsWithRef } from "react";

/**
 * Action hierarchy (FR-037): one primary action per task context, secondary for the rest, destructive looks different
 * and never sits as the default. A busy button keeps its size and says it is working (FR-039).
 */
export type ButtonVariant = "primary" | "secondary" | "destructive" | "quiet";

export function Button({
  variant = "secondary",
  busy = false,
  busyLabel,
  children,
  className,
  type = "button",
  ...rest
}: ComponentPropsWithRef<"button"> & { variant?: ButtonVariant; busy?: boolean; busyLabel?: string }) {
  return (
    <button
      type={type}
      className={["button", `button-${variant}`, className].filter(Boolean).join(" ")}
      data-variant={variant}
      aria-busy={busy || undefined}
      aria-disabled={busy || rest.disabled || undefined}
      {...rest}
      onClick={busy ? (e) => e.preventDefault() : rest.onClick}
    >
      {busy && busyLabel ? busyLabel : children}
    </button>
  );
}
