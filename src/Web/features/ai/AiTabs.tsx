import Link from "next/link";
import { getTranslations } from "next-intl/server";

export type AiTab = "policy" | "routing" | "usage" | "debug";

/** The AI governance pages (spec 013 section 10, T016). Each page's data is authorized by the API on its own. */
export async function AiTabs({ current }: { current: AiTab }) {
  const t = await getTranslations("ai");
  const tabs = [
    { key: "policy", href: "/admin/ai" },
    { key: "routing", href: "/admin/ai/routing" },
    { key: "usage", href: "/admin/ai/usage" },
    { key: "debug", href: "/admin/ai/debug" },
  ] as const;
  return (
    <nav aria-label={t("tabsLabel")} className="ai-tabs">
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
