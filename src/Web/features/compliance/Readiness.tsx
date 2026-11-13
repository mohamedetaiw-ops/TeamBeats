import Link from "next/link";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { Meter } from "@/components/ui/Status";
import { controlHref, controlStatuses, pick, type ReadinessView } from "@/lib/compliance";
import "./compliance.css";

const tones = { not_started: "warning", in_progress: "accent", evidence_ready: "accent", reviewed: "success" } as const;

/**
 * The readiness view (US7 scenario 3): controls by status, overdue controls and evidence tasks, and the same counts by
 * owner and by the owner's org unit. Tables carry the numbers, so nothing depends on color alone.
 */
export async function Readiness({ view }: { view: ReadinessView }) {
  const [t, locale, format] = await Promise.all([getTranslations("compliance"), getLocale(), getFormatter()]);
  const statusCells = (counts: Record<string, number>) => controlStatuses.map((s) => <td key={s} data-number>{format.number(counts[s] ?? 0)}</td>);
  return (
    <section className="compliance-section" aria-labelledby="compliance-readiness">
      <h2 id="compliance-readiness">{t("readiness.title")}</h2>
      <div className="admin-grid">
        {controlStatuses.map((s) => (
          <Meter
            key={s}
            label={t(`status.${s}`)}
            value={view.byStatus[s] ?? 0}
            max={Math.max(view.total, 1)}
            tone={tones[s]}
            valueText={t("readiness.ofTotal", { count: view.byStatus[s] ?? 0, total: view.total })}
          />
        ))}
      </div>
      <p className="compliance-meta">{t("readiness.overdueControls", { count: view.overdueControls })}</p>

      <h3>{t("readiness.overdueTasks")}</h3>
      {view.overdueTasks.length === 0 ? (
        <p className="compliance-meta">{t("readiness.noOverdue")}</p>
      ) : (
        <ul className="compliance-list">
          {view.overdueTasks.map((task) => (
            <li key={task.taskId}>
              <Link href={controlHref(task.controlId)}>
                <bdi dir="ltr">{task.controlCode}</bdi> <bdi>{task.title}</bdi>
              </Link>{" "}
              <span className="compliance-meta">
                {t("readiness.dueBy", {
                  date: format.dateTime(new Date(`${task.dueOn}T12:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }),
                  owner: task.owner?.displayName ?? t("noOwner"),
                })}
              </span>
            </li>
          ))}
        </ul>
      )}

      <div className="compliance-table">
        <table>
          <caption>{t("readiness.byOwner")}</caption>
          <thead>
            <tr>
              <th scope="col">{t("readiness.owner")}</th>
              {controlStatuses.map((s) => (
                <th scope="col" key={s}>
                  {t(`status.${s}`)}
                </th>
              ))}
              <th scope="col">{t("readiness.overdueTasks")}</th>
            </tr>
          </thead>
          <tbody>
            {view.byOwner.map((o) => (
              <tr key={o.owner?.userId ?? "none"}>
                <th scope="row">
                  <bdi>{o.owner?.displayName ?? t("noOwner")}</bdi>
                </th>
                {statusCells(o.byStatus)}
                <td data-number>{format.number(o.overdueTasks)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="compliance-table">
        <table>
          <caption>{t("readiness.byUnit")}</caption>
          <thead>
            <tr>
              <th scope="col">{t("readiness.unit")}</th>
              {controlStatuses.map((s) => (
                <th scope="col" key={s}>
                  {t(`status.${s}`)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {view.byOrgUnit.map((u) => (
              <tr key={u.orgUnitId ?? "none"}>
                <th scope="row">
                  <bdi>{u.names ? pick(u.names, locale) : t("readiness.noUnit")}</bdi>
                </th>
                {statusCells(u.byStatus)}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
