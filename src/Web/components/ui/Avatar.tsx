/**
 * Person avatar: a photo when one is given, otherwise initials. The person's name is the accessible name, so a list of
 * avatars reads as a list of people. Initials take the first letter of the first two words, in any script.
 */
export function Avatar({ name, src, size = "md" }: { name: string; src?: string | null; size?: "sm" | "md" | "lg" }) {
  const initials = name
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((w) => Array.from(w)[0] ?? "")
    .join("");
  return (
    <span className="ui-avatar" data-size={size} role="img" aria-label={name}>
      {src ? (
        // Avatars are small session-protected API files; the image optimizer cannot fetch them.
        // eslint-disable-next-line @next/next/no-img-element
        <img src={src} alt="" loading="lazy" decoding="async" />
      ) : (
        <span aria-hidden="true">{initials}</span>
      )}
    </span>
  );
}

/**
 * A short row of overlapping avatars for the people on a card or a thread (spec 036 DLS-038). It shows at most `max`
 * people and then how many more there are; the group has a name, and the count is text, not a picture.
 */
export function AvatarGroup({
  label,
  people,
  max = 4,
  more,
  size = "sm",
}: {
  label: string;
  people: readonly { name: string; src?: string | null }[];
  max?: number;
  /** The text for the people not shown, such as "+3 more". The caller formats it with the count. */
  more?: (count: number) => string;
  size?: "sm" | "md";
}) {
  const shown = people.slice(0, max);
  const rest = people.length - shown.length;
  return (
    <ul className="ui-avatar-group" aria-label={label}>
      {shown.map((person, i) => (
        <li key={`${person.name}-${i}`}>
          <Avatar name={person.name} src={person.src} size={size} />
        </li>
      ))}
      {rest > 0 && more ? (
        <li>
          <span className="ui-avatar ui-avatar-more" data-size={size}>
            {more(rest)}
          </span>
        </li>
      ) : null}
    </ul>
  );
}
