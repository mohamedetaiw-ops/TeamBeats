import Link from "next/link";
import { getTranslations } from "next-intl/server";

export type AgentTab = "agents" | "policy" | "units" | "runs" | "security";

/** The agent administration pages (specs 020 and 027). Each page's data is authorized by the API on its own. */
export async function AgentTabs({ current }: { current: AgentTab }) {
  const t = await getTranslations("agents");
  const tabs = [
    { key: "agents", href: "/admin/agents" },
    { key: "policy", href: "/admin/agents/policy" },
    { key: "units", href: "/admin/agents/policy/units" },
    { key: "runs", href: "/admin/agents/runs" },
    { key: "security", href: "/admin/agents/security" },
  ] as const;
  return (
    <nav aria-label={t("tabsLabel")} className="agents-tabs">
      <ul>
        {tabs.map((tab) => (
          <li key={tab.key}>
            <Link href={tab.href} aria-current={tab.key === current ? "page" : undefined}>
              {t(`tabs.${tab.key}`)}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
