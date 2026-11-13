import Link from "next/link";
import { getLocale, getTranslations } from "next-intl/server";
import { Badge } from "@/components/ui/Badge";
import { Progress } from "@/components/ui/Status";
import { StateView } from "@/components/ui/StateView";
import { controlStatuses, pick, projectHref, type ProjectSummary } from "@/lib/compliance";
import "./compliance.css";

/**
 * Compliance projects as cards (spec 026 US7): framework, scope, how many controls are reviewed, overdue tasks, and
 * the space a project is shared to. The API lists only what the viewer may see; shared projects say they are read-only.
 */
export async function ProjectList({ projects, headingLevel = 2 }: { projects: ProjectSummary[]; headingLevel?: 2 | 3 }) {
  const [t, locale] = await Promise.all([getTranslations("compliance"), getLocale()]);
  if (projects.length === 0) {
    return <StateView kind="empty" title={t("list.noneTitle")} message={t("list.none")} icon="policy" />;
  }
  const Heading = `h${headingLevel}` as const;
  return (
    <ul className="compliance-grid" aria-label={t("list.label")}>
      {projects.map((p) => {
        const total = controlStatuses.reduce((sum, s) => sum + (p.byStatus[s] ?? 0), 0);
        const reviewed = p.byStatus.reviewed ?? 0;
        return (
          <li key={p.id} className="compliance-card">
            <Heading>
              <Link href={projectHref(p.id)}>
                <bdi>{p.name}</bdi>
              </Link>
            </Heading>
            <p className="compliance-meta">
              <bdi>{pick(p.frameworkNames, locale)}</bdi> <bdi dir="ltr">{p.frameworkVersion}</bdi>
            </p>
            <p className="compliance-meta">{p.orgUnitNames ? t("list.scope", { unit: pick(p.orgUnitNames, locale) }) : t("list.wholeTenant")}</p>
            <Progress label={t("list.reviewed")} value={reviewed} max={Math.max(total, 1)} valueText={t("list.reviewedOf", { reviewed, total })} />
            <div className="compliance-row">
              {p.status === "archived" ? <Badge tone="neutral">{t("status.archived")}</Badge> : null}
              {p.overdueTasks > 0 ? (
                <Badge tone="warning" icon="clock">
                  {t("list.overdue", { count: p.overdueTasks })}
                </Badge>
              ) : null}
              {p.rights.readOnly ? <Badge tone="info" icon="eye">{t("list.readOnly")}</Badge> : null}
              {p.sharedSpace ? (
                <Badge tone="accent" icon="folder">
                  {t("list.sharedTo", { space: p.sharedSpace.name })}
                </Badge>
              ) : null}
            </div>
          </li>
        );
      })}
    </ul>
  );
}
