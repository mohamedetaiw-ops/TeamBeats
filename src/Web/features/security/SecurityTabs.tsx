import Link from "next/link";
import { getTranslations } from "next-intl/server";

/** The three security pages (spec 026 T016). The API authorizes each page's data on its own. */
export async function SecurityTabs({ current, openAlerts }: { current: "policy" | "alerts" | "posture"; openAlerts?: number | null }) {
  const t = await getTranslations("security");
  const tabs = [
    { key: "policy", href: "/admin/security" },
    { key: "alerts", href: "/admin/security/alerts" },
    { key: "posture", href: "/admin/security/posture" },
  ] as const;
  return (
    <nav aria-label={t("tabsLabel")} className="security-tabs">
      <ul>
        {tabs.map((tab) => (
          <li key={tab.key}>
            <Link href={tab.href} aria-current={tab.key === current ? "page" : undefined}>
              {t(`tabs.${tab.key}`)}
              {tab.key === "alerts" && openAlerts ? (
                <span className="admin-nav-badge" aria-label={t("openAlerts", { count: openAlerts })}>
                  {openAlerts}
                </span>
              ) : null}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
