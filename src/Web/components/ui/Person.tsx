import Link from "next/link";
import type { ReactNode } from "react";
import { Icon, type IconName } from "./Icon";

/** `solid` is the signed-in person in the shell bar and its account menu. */
export type PersonTone = "accent" | "highlight" | "success" | "info" | "solid";

/** Initials from the first letter of the first two words, in any script. */
function initialsOf(name: string) {
  return name
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((w) => Array.from(w)[0] ?? "")
    .join("");
}

/**
 * The avatar beside a person's name (spec 036 DLS-062): the photo when there is one, otherwise initials on a steady
 * tint. The name next to it is what people read, so the avatar is hidden from assistive technology.
 */
export function PersonAvatar({ name, src, tone, size = "md" }: { name: string; src?: string | null; tone?: PersonTone; size?: "xs" | "md" | "card" | "lg" }) {
  return (
    <span className="ui-person-avatar" data-size={size} data-tone={src ? undefined : tone} aria-hidden="true">
      {src ? (
        // A small session-protected API file; the image optimizer cannot fetch it. The name is next to it, so alt is empty.
        // eslint-disable-next-line @next/next/no-img-element
        <img src={src} alt="" loading="lazy" decoding="async" />
      ) : (
        initialsOf(name)
      )}
    </span>
  );
}

export type PersonView = {
  name: string;
  href: string;
  avatar: { src?: string | null; tone?: PersonTone };
  /** The job title, or the words for no title shown quietly. */
  title?: { text: string; missing?: boolean } | null;
  /** One meta line, already joined: unit · location · anything the feature adds. */
  meta?: string | null;
  /** Up to two quick actions, such as email and call, for a card. */
  actions?: readonly { icon: IconName; label: string; href: string }[];
};

/**
 * The person part (spec 036 DLS-062, DLS-113) in its three forms. Chip: avatar and name. Row: avatar, the name linked
 * to the profile, the title on its own line and one meta line. Card: the row in a frame that the name link covers, with
 * the quick actions above it. The feature passes only what the viewer may see.
 */
export function PersonEntry({ person, form = "row" }: { person: PersonView; form?: "chip" | "row" | "card" }) {
  if (form === "chip") {
    return (
      <Link href={person.href} className="ui-person-chip">
        <PersonAvatar name={person.name} src={person.avatar.src} tone={person.avatar.tone} size="xs" />
        <bdi>{person.name}</bdi>
      </Link>
    );
  }
  const actions = form === "card" ? (person.actions ?? []).slice(0, 2) : [];
  return (
    <div className="ui-person" data-form={form}>
      <PersonAvatar name={person.name} src={person.avatar.src} tone={person.avatar.tone} size={form === "card" ? "card" : "md"} />
      <div className="ui-person-text">
        <Link href={person.href} className="ui-person-name">
          <bdi>{person.name}</bdi>
        </Link>
        {person.title ? (
          <span className={person.title.missing ? "ui-person-meta" : undefined}>
            <bdi>{person.title.text}</bdi>
          </span>
        ) : null}
        {person.meta ? (
          <span className="ui-person-meta">
            <bdi>{person.meta}</bdi>
          </span>
        ) : null}
      </div>
      {actions.length > 0 ? (
        <span className="ui-person-actions">
          {actions.map((a) => (
            <a key={a.href} className="button button-quiet ui-icon-button" data-size="sm" href={a.href} aria-label={a.label} title={a.label}>
              <Icon name={a.icon} size={16} />
            </a>
          ))}
        </span>
      ) : null}
    </div>
  );
}

/** A list of people: a plain stack, cards on a grid, or rows with a line between them. */
export function PeopleList({ layout = "stack", testId, children }: { layout?: "stack" | "cards" | "rows"; testId?: string; children: ReactNode }) {
  return (
    <ul className="ui-people" data-layout={layout} data-testid={testId}>
      {children}
    </ul>
  );
}
