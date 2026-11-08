import Link from "next/link";
import { useTranslations } from "next-intl";
import "./services.css";

/** Links between the services pages, with the current one marked. */
export function ServicesNav({ current }: { current: "catalogue" | "requests" | "team" | "tasks" | null }) {
  const t = useTranslations("services.nav");
  const links = [
    { key: "catalogue", href: "/services" },
    { key: "requests", href: "/requests" },
    { key: "team", href: "/requests/team" },
    { key: "tasks", href: "/tasks" },
  ] as const;
  return (
    <nav aria-label={t("label")} className="services-nav">
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
