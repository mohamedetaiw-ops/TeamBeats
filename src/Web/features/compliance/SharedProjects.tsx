import Link from "next/link";
import { getLocale, getTranslations } from "next-intl/server";
import { Badge } from "@/components/ui/Badge";
import { pick, projectHref, type ProjectSummary } from "@/lib/compliance";
import "./compliance.css";

/**
 * Compliance projects shared to this space (FR-024), on the space page for its readers: auditor guests open them
 * read-only from here. Nothing shows when none is shared or the plan has no compliance projects.
 */
export async function SharedProjects({ projects }: { projects: ProjectSummary[] }) {
  if (projects.length === 0) return null;
  const [t, locale] = await Promise.all([getTranslations("compliance.shared"), getLocale()]);
  return (
    <section className="compliance-section" aria-labelledby="space-compliance">
      <h2 id="space-compliance">{t("title")}</h2>
      <p className="compliance-meta">{t("intro")}</p>
      <ul className="compliance-list">
        {projects.map((p) => (
          <li key={p.id} className="compliance-row">
            <Link href={projectHref(p.id)}>
              <bdi>{p.name}</bdi>
            </Link>
            <span className="compliance-meta">
              <bdi>{pick(p.frameworkNames, locale)}</bdi>
            </span>
            {p.rights.readOnly ? <Badge tone="info" icon="eye">{t("readOnly")}</Badge> : null}
          </li>
        ))}
      </ul>
    </section>
  );
}
