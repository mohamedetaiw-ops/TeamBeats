import Link from "next/link";
import { useTranslations } from "next-intl";
import { joinMeta } from "@/lib/format";
import { Icon } from "@/components/ui/Icon";
import { initials, localName, locationName, personName, type PersonCard } from "@/lib/people/types";

const tones = ["accent", "highlight", "success", "info"] as const;

/** A steady colour per person, from the ID, so the same colleague always has the same avatar colour. */
function avatarTone(id: string) {
  let sum = 0;
  for (const c of id) sum = (sum + c.charCodeAt(0)) % 997;
  return tones[sum % tones.length];
}

/** A channel value the API gave for this person; it sends only what the viewer may see. */
function field(person: PersonCard, key: string): string | null {
  return person.fields?.find((f) => f.key === key)?.value || null;
}

/**
 * The person part (spec 036 DLS-062, DLS-113) in its three forms. Chip: avatar and name. Row: avatar, the name linked
 * to the profile, the title on its own line, and unit · location on one meta line. Card: the row in a frame, with up to
 * two quick actions for the channels the person has. The API has already removed anything the viewer may not see, so
 * this shows what it is given.
 */
export function PersonItem({ person, locale, extra, form = "row" }: { person: PersonCard; locale: string; extra?: string; form?: "chip" | "row" | "card" }) {
  const t = useTranslations("people");
  const name = personName(person, locale);
  if (form === "chip") {
    return (
      <Link href={`/people/${person.id}`} className="people-chip">
        <span className="people-avatar" data-tone={avatarTone(person.id)} aria-hidden="true">
          {initials(name)}
        </span>
        <bdi>{name}</bdi>
      </Link>
    );
  }
  const meta = joinMeta([person.unit ? localName(person.unit.names, locale) : null, person.location ? locationName(person.location, locale) : null, extra]);
  const email = form === "card" ? field(person, "email") : null;
  const phone = form === "card" ? (field(person, "workPhone") ?? field(person, "mobilePhone")) : null;
  return (
    <div className="people-item" data-form={form}>
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
        ) : (
          <span className="people-meta">{t("noTitle")}</span>
        )}
        {meta ? (
          <span className="people-meta">
            <bdi>{meta}</bdi>
          </span>
        ) : null}
      </div>
      {email || phone ? (
        <span className="people-quick">
          {email ? (
            <a className="button button-quiet ui-icon-button" data-size="sm" href={`mailto:${email}`} aria-label={t("quick.email", { name })} title={t("quick.email", { name })}>
              <Icon name="mail" size={16} />
            </a>
          ) : null}
          {phone ? (
            <a className="button button-quiet ui-icon-button" data-size="sm" href={`tel:${phone.replace(/[^\d+]/gu, "")}`} aria-label={t("quick.call", { name })} title={t("quick.call", { name })}>
              <Icon name="phone" size={16} />
            </a>
          ) : null}
        </span>
      ) : null}
    </div>
  );
}
