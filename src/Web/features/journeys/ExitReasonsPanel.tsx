import { getLocale, getTranslations } from "next-intl/server";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import type { ExitReasons } from "@/lib/journeys";
import "./journeys.css";

/**
 * Exit reasons (spec 022 FR-018, spec 021 US3 scenario 3): counts of the structured reasons from exit interviews in the
 * officer's units over the last twelve months. The API merges small units into "other" and holds back any group below
 * the minimum size; free text is never part of it.
 */
export async function ExitReasonsPanel({ data, units }: { data: ExitReasons; units: OrgUnit[] }) {
  const t = await getTranslations("journeys");
  const locale = await getLocale();
  const label = (id: string | null) => {
    if (!id) return t("exitReasons.other");
    const unit = units.find((u) => u.id === id);
    return unit ? unitName(unit, locale) : id;
  };
  return (
    <section className="journeys-section" aria-labelledby="journey-exit-reasons">
      <h2 id="journey-exit-reasons">{t("exitReasons.title")}</h2>
      <p className="field-hint">{t("exitReasons.intro", { size: data.minimumGroupSize, interviews: data.interviews, declined: data.declined })}</p>
      {data.reasons.length === 0 ? (
        <p className="field-hint">{t("exitReasons.notEnough", { size: data.minimumGroupSize })}</p>
      ) : (
        <div className="journeys-table">
          <table>
            <caption className="visually-hidden">{t("exitReasons.title")}</caption>
            <thead>
              <tr>
                <th scope="col">{t("exitReasons.reason")}</th>
                <th scope="col">{t("exitReasons.chosen")}</th>
                <th scope="col">{t("exitReasons.primary")}</th>
              </tr>
            </thead>
            <tbody>
              {data.reasons.map((r) => (
                <tr key={r.reason}>
                  <td>{t(`detail.reasons.${r.reason}`)}</td>
                  <td>{r.chosen}</td>
                  <td>{r.primary}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {data.units.length > 0 ? (
        <ul className="journeys-list">
          {data.units.map((u) => (
            <li key={u.orgUnitId ?? "other"} className="journeys-item">
              <bdi>{label(u.orgUnitId)}</bdi>
              <span className="field-hint">{t("exitReasons.unitCount", { interviews: u.interviews })}</span>
            </li>
          ))}
        </ul>
      ) : null}
      {data.hidden > 0 ? <p className="field-hint">{t("exitReasons.hidden", { count: data.hidden })}</p> : null}
    </section>
  );
}
