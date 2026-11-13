import Link from "next/link";
import { getTranslations } from "next-intl/server";

/** The security pages (spec 026 T016, and SIEM streaming in R2). The API authorizes each page's data on its own. */
export async function SecurityTabs({ current, openAlerts }: { current: "policy" | "alerts" | "posture" | "siem" | "reviews"; openAlerts?: number | null }) {
  const t = await getTranslations("security");
  const tabs = [
    { key: "policy", href: "/admin/security" },
    { key: "alerts", href: "/admin/security/alerts" },
    { key: "posture", href: "/admin/security/posture" },
    { key: "siem", href: "/admin/security/siem" },
    { key: "reviews", href: "/admin/security/reviews" },
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
