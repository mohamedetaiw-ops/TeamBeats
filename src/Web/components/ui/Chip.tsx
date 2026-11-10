import type { ReactNode } from "react";
import { useTranslations } from "next-intl";
import { Icon, type IconName } from "./Icon";

export type ChipTone = "neutral" | "accent" | "info" | "success" | "warning" | "danger";

/**
 * Chip (spec 036 DLS-034): a compact token for a value people chose or can choose, such as a filter, a skill or a
 * recipient. Three forms:
 *  - static: just the value;
 *  - removable (`onRemove`): the value with a remove button named after it;
 *  - selectable (`selected` and `onToggle`): a toggle button that says whether it is pressed.
 * A status that people cannot change is a Badge, not a chip.
 */
export function Chip({
  children,
  tone = "neutral",
  icon,
  onRemove,
  removeLabel,
  selected,
  onToggle,
  disabled,
}: {
  children: ReactNode;
  tone?: ChipTone;
  icon?: IconName;
  onRemove?: () => void;
  /** The accessible name of the remove button, for example "Remove Finance". Required when the chip text is not plain text. */
  removeLabel?: string;
  selected?: boolean;
  onToggle?: (selected: boolean) => void;
  disabled?: boolean;
}) {
  const t = useTranslations("ui.controls");
  if (onToggle) {
    return (
      <button type="button" className="ui-chip" data-tone={tone} data-selectable aria-pressed={selected ?? false} disabled={disabled} onClick={() => onToggle(!selected)}>
        {selected ? <Icon name="check" size={14} /> : icon ? <Icon name={icon} size={14} /> : null}
        <span>{children}</span>
      </button>
    );
  }
  return (
    <span className="ui-chip" data-tone={tone}>
      {icon ? <Icon name={icon} size={14} /> : null}
      <span>{children}</span>
      {onRemove ? (
        <button
          type="button"
          className="ui-chip-remove"
          aria-label={removeLabel ?? (typeof children === "string" ? t("remove", { name: children }) : t("removeItem"))}
          disabled={disabled}
          onClick={onRemove}
        >
          <Icon name="close" size={14} />
        </button>
      ) : null}
    </span>
  );
}

/**
 * Tag group: a named list of chips, such as the filters in force or the skills on a profile. The list semantics let a
 * screen reader say how many there are; an empty group shows the caller's own text.
 */
export function TagGroup({
  label,
  items,
  onRemove,
  empty,
  tone,
}: {
  label: string;
  items: readonly { key: string; label: string; icon?: IconName }[];
  onRemove?: (key: string) => void;
  empty?: string;
  tone?: ChipTone;
}) {
  if (items.length === 0) {
    return empty ? <p className="notice">{empty}</p> : null;
  }
  return (
    <ul className="ui-tag-group" aria-label={label}>
      {items.map((item) => (
        <li key={item.key}>
          <Chip tone={tone} icon={item.icon} onRemove={onRemove ? () => onRemove(item.key) : undefined}>
            {item.label}
          </Chip>
        </li>
      ))}
    </ul>
  );
}
