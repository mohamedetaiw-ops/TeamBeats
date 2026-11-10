import Link from "next/link";
import { initials, localName, locationName, personName, type PersonCard } from "@/lib/people/types";

const tones = ["accent", "highlight", "success", "info"] as const;

/** A steady colour per person, from the ID, so the same colleague always has the same avatar colour. */
function avatarTone(id: string) {
  let sum = 0;
  for (const c of id) sum = (sum + c.charCodeAt(0)) % 997;
  return tones[sum % tones.length];
}

/**
 * One person in a list: initials, name linked to the profile, title, unit and location. The API has already removed
 * anything the viewer may not see, so this shows what it is given.
 */
export function PersonItem({ person, locale, extra }: { person: PersonCard; locale: string; extra?: string }) {
  const name = personName(person, locale);
  return (
    <div className="people-item">
      <span className="people-avatar" data-tone={avatarTone(person.id)} aria-hidden="true">
        {initials(name)}
      </span>
      <div className="people-item-text">
        <Link href={`/people/${person.id}`} className="people-item-name">
          <bdi>{name}</bdi>
        </Link>
        {person.jobTitle ? (
          <span>
            <bdi>{person.jobTitle}</bdi>
          </span>
        ) : null}
        <span className="people-meta">
          {person.unit ? <bdi>{localName(person.unit.names, locale)}</bdi> : null}
          {person.location ? <bdi>{locationName(person.location, locale)}</bdi> : null}
          {extra ? <span>{extra}</span> : null}
        </span>
      </div>
    </div>
  );
}
