import { getLocale, getTranslations } from "next-intl/server";
import type { NavigationSection } from "@/lib/api";
import { Icon } from "@/components/ui/Icon";
import { AdminNavLink } from "./AdminNavLink";

/** The admin navigation groups in the order they show (spec 006 FR-017). The API names each section's group. */
export const adminGroups = ["workspace", "people", "hr", "content", "security", "ai"] as const;

/** Sections under their group headings, sorted by localized name inside a group; a group with no section is left out. */
export async function groupSections(sections: readonly NavigationSection[]) {
  const [t, locale] = await Promise.all([getTranslations(), getLocale()]);
  return adminGroups
    .map((group) => ({
      group,
      sections: sections
        .filter((s) => (adminGroups.includes(s.group as (typeof adminGroups)[number]) ? s.group : "workspace") === group)
        .map((s) => ({ ...s, label: t(s.labelKey) }))
        .sort((a, b) => a.label.localeCompare(b.label, locale)),
    }))
    .filter((g) => g.sections.length > 0);
}

export async function AdminNav({ label, sections }: { label: string; sections: NavigationSection[] }) {
  const t = await getTranslations("admin");
  const groups = await groupSections(sections);
  const paths = sections.map((s) => s.path);
  return (
    <nav aria-label={label} className="admin-nav">
      {groups.map(({ group, sections: items }) => (
        <section key={group} className="admin-nav-group" aria-labelledby={`admin-nav-${group}`}>
          <h2 id={`admin-nav-${group}`} className="admin-nav-heading">
            {t(`navGroups.${group}`)}
          </h2>
          <ul>
            {items.map((s) => (
              <li key={s.key}>
                <AdminNavLink href={s.path} paths={paths}>
                  <span>{s.label}</span>
                  {s.badge ? (
                    <span className="admin-nav-badge" aria-label={t("navBadge", { count: s.badge })}>
                      {s.badge}
                    </span>
                  ) : null}
                  <Icon name="chevron-forward" mirrorInRtl />
                </AdminNavLink>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </nav>
  );
}
