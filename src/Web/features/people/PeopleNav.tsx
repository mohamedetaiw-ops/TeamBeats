import Link from "next/link";
import { useTranslations } from "next-intl";

/** Links between the people pages, with the current one marked. */
export function PeopleNav({ current }: { current: "directory" | "team" | "me" | null }) {
  const t = useTranslations("people.nav");
  const links = [
    { key: "directory", href: "/people" },
    { key: "team", href: "/people/team" },
    { key: "me", href: "/people/me" },
  ] as const;
  return (
    <nav aria-label={t("label")} className="people-nav">
      <ul>
        {links.map((l) => (
          <li key={l.key}>
            <Link href={l.href} aria-current={current === l.key ? "page" : undefined}>
              {t(l.key)}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
