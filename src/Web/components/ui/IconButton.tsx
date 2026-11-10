import type { ComponentPropsWithRef } from "react";
import { useTranslations } from "next-intl";
import { Icon, type IconName } from "./Icon";
import type { ButtonSize, ButtonVariant } from "./Button";

const iconSize: Record<ButtonSize, number> = { md: 20, sm: 18, xs: 14 };

/**
 * A square button that shows only an icon (spec 036 DLS-033). The `label` is required: it is the accessible name and
 * the tooltip, so the action is never a mystery glyph. Use a text button when there is room for words.
 */
export function IconButton({
  icon,
  label,
  variant = "quiet",
  size = "md",
  className,
  type = "button",
  ...rest
}: Omit<ComponentPropsWithRef<"button">, "children" | "aria-label"> & { icon: IconName; label: string; variant?: ButtonVariant; size?: ButtonSize }) {
  return (
    <button
      type={type}
      className={["button", `button-${variant}`, "ui-icon-button", className].filter(Boolean).join(" ")}
      data-variant={variant}
      data-size={size === "md" ? undefined : size}
      aria-label={label}
      title={label}
      {...rest}
    >
      <Icon name={icon} size={iconSize[size]} />
    </button>
  );
}

/** The one way to close or dismiss a panel, banner, chip or dialog. It says "Close" unless the caller names what closes. */
export function CloseButton({ label, size = "sm", ...rest }: Omit<ComponentPropsWithRef<"button">, "children" | "aria-label"> & { label?: string; size?: ButtonSize }) {
  const t = useTranslations("ui.controls");
  return <IconButton icon="close" label={label ?? t("close")} size={size} {...rest} />;
}
