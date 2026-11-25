import { useTranslations } from "next-intl";
import { joinMeta } from "@/lib/format";
import { PersonEntry, type PersonTone } from "@/components/ui/Person";
import { localName, locationName, personName, photoAt, type PersonCard } from "@/lib/people/types";

const tones = ["accent", "highlight", "success", "info"] as const;

/** A steady colour per person, from the ID, so the same colleague always has the same avatar colour. */
export function avatarTone(id: string): PersonTone {
  let sum = 0;
  for (const c of id) sum = (sum + c.charCodeAt(0)) % 997;
  return tones[sum % tones.length]!;
}

/** A channel value the API gave for this person; it sends only what the viewer may see. */
function field(person: PersonCard, key: string): string | null {
  return person.fields?.find((f) => f.key === key)?.value || null;
}

/**
 * A directory person on the shared person part (spec 036 DLS-062, DLS-113): chip, row or card. The API has already
 * removed anything the viewer may not see, so this maps what it is given; a card offers email and call when the
 * person has them.
 */
export function PersonItem({ person, locale, extra, form = "row" }: { person: PersonCard; locale: string; extra?: string; form?: "chip" | "row" | "card" }) {
  const t = useTranslations("people");
  const name = personName(person, locale);
  const size = form === "chip" ? 24 : form === "card" ? 48 : 40;
  const email = form === "card" ? field(person, "email") : null;
  const phone = form === "card" ? (field(person, "workPhone") ?? field(person, "mobilePhone")) : null;
  const actions = [
    ...(email ? [{ icon: "mail" as const, label: t("quick.email", { name }), href: `mailto:${email}` }] : []),
    ...(phone ? [{ icon: "phone" as const, label: t("quick.call", { name }), href: `tel:${phone.replace(/[^\d+]/gu, "")}` }] : []),
  ];
  return (
    <PersonEntry
      form={form}
      person={{
        name,
        href: `/people/${person.id}`,
        avatar: { src: photoAt(person.photo, size), tone: avatarTone(person.id) },
        title: person.jobTitle ? { text: person.jobTitle } : { text: t("noTitle"), missing: true },
        meta: joinMeta([person.unit ? localName(person.unit.names, locale) : null, person.location ? locationName(person.location, locale) : null, extra]),
        actions,
      }}
    />
  );
}
