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
