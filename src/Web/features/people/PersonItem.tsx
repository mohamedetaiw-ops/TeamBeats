import Link from "next/link";
import { joinMeta } from "@/lib/format";
import { initials, localName, locationName, personName, type PersonCard } from "@/lib/people/types";

const tones = ["accent", "highlight", "success", "info"] as const;

/** A steady colour per person, from the ID, so the same colleague always has the same avatar colour. */
function avatarTone(id: string) {
  let sum = 0;
  for (const c of id) sum = (sum + c.charCodeAt(0)) % 997;
  return tones[sum % tones.length];
}

/**
 * One person in a list (spec 036 DLS-062): initials aligned to the top of the text, the name linked to the profile, the
 * title on its own line, and unit · location on one meta line. The API has already removed
 * anything the viewer may not see, so this shows what it is given.
 */
export function PersonItem({ person, locale, extra }: { person: PersonCard; locale: string; extra?: string }) {
  const name = personName(person, locale);
  const meta = joinMeta([person.unit ? localName(person.unit.names, locale) : null, person.location ? locationName(person.location, locale) : null, extra]);
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
        {meta ? (
          <span className="people-meta">
            <bdi>{meta}</bdi>
          </span>
        ) : null}
      </div>
    </div>
  );
}
