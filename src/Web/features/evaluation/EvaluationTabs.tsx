import Link from "next/link";
import { getTranslations } from "next-intl/server";

export type EvaluationTab = "agents" | "estate" | "production" | "review" | "outcomes" | "calibration" | "units" | "settings";

/** The evaluation pages (spec 034). Each page's data is authorized by the API on its own. */
export async function EvaluationTabs({ current }: { current: EvaluationTab }) {
  const t = await getTranslations("evaluation");
  const tabs = [
    { key: "agents", href: "/admin/evaluation" },
    { key: "estate", href: "/admin/evaluation/estate" },
    { key: "production", href: "/admin/evaluation/production" },
    { key: "review", href: "/admin/evaluation/review" },
    { key: "outcomes", href: "/admin/evaluation/outcomes" },
    { key: "calibration", href: "/admin/evaluation/calibration" },
    { key: "units", href: "/admin/evaluation/units" },
    { key: "settings", href: "/admin/evaluation/settings" },
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
