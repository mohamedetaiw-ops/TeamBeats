import Link from "next/link";
import type { ComponentPropsWithRef, ReactNode } from "react";
import { Icon, type IconName } from "./Icon";

/**
 * Action hierarchy (FR-037): one primary action per task context, secondary for the rest, destructive looks different
 * and never sits as the default. A busy button keeps its size and says it is working (FR-039).
 *
 * Sizes (spec 036 DLS-033): medium is the 44 px default for the actions of a page or form; small (36 px) and compact
 * (28 px) are for toolbars, table rows and dense panels only, never for the main action of a page.
 */
export type ButtonVariant = "primary" | "secondary" | "destructive" | "quiet";
export type ButtonSize = "md" | "sm" | "xs";

type Look = { variant?: ButtonVariant; size?: ButtonSize; icon?: IconName; iconEnd?: IconName };

const iconSize: Record<ButtonSize, number> = { md: 18, sm: 16, xs: 14 };

function classes(variant: ButtonVariant, className?: string) {
  return ["button", `button-${variant}`, className].filter(Boolean).join(" ");
}

/** Icons sit in the reading order: `icon` before the label, `iconEnd` after it. They are decorative; the label names the action. */
function content(size: ButtonSize, children: ReactNode, icon?: IconName, iconEnd?: IconName) {
  if (!icon && !iconEnd) return children;
  return (
    <>
      {icon ? <Icon name={icon} size={iconSize[size]} /> : null}
      <span>{children}</span>
      {iconEnd ? <Icon name={iconEnd} size={iconSize[size]} /> : null}
    </>
  );
}

export function Button({
  variant = "secondary",
  size = "md",
  icon,
  iconEnd,
  busy = false,
  busyLabel,
  children,
  className,
  type = "button",
  ...rest
}: ComponentPropsWithRef<"button"> & Look & { busy?: boolean; busyLabel?: string }) {
  return (
    <button
      type={type}
      className={classes(variant, className)}
      data-variant={variant}
      data-size={size === "md" ? undefined : size}
      data-icon={icon || iconEnd ? true : undefined}
      aria-busy={busy || undefined}
      aria-disabled={busy || rest.disabled || undefined}
      {...rest}
      onClick={busy ? (e) => e.preventDefault() : rest.onClick}
    >
      {busy && busyLabel ? busyLabel : content(size, children, icon, iconEnd)}
    </button>
  );
}

/** The same looks for navigation. An action that goes to another page is a link, so it opens in a new tab and shows its address. */
export function ButtonLink({
  variant = "secondary",
  size = "md",
  icon,
  iconEnd,
  children,
  className,
  href,
  ...rest
}: Omit<ComponentPropsWithRef<"a">, "href"> & Look & { href: string }) {
  return (
    <Link href={href} className={classes(variant, className)} data-variant={variant} data-size={size === "md" ? undefined : size} data-icon={icon || iconEnd ? true : undefined} {...rest}>
      {content(size, children, icon, iconEnd)}
    </Link>
  );
}
