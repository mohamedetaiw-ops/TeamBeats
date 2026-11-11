"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { HR_CANCEL_REASONS, HR_CHANGE_KINDS, HR_CHANGE_STATUSES, known, type HrChange, type HrOrgCode } from "@/lib/integrations/types";
import { problemMessages } from "@/lib/notifications/types";
import "./hr-integration.css";

const BASE = "/api/v1/hr/integration";

type Result = { ok: boolean; messages: string[] } | null;

/**
 * HR lifecycle on the HR integration page (spec 019 US2 to US4, FR-007): the org codes HR uses with the unit each is
 * mapped to, codes waiting for a unit first, and the joiners, moves, leavers and rehires that are waiting, applied or
 * cancelled. Mapping a code applies the moves that waited for it. The API checks every mapping.
 */
export function HrLifecyclePanel({ initialCodes, initialChanges, units }: { initialCodes: HrOrgCode[]; initialChanges: HrChange[]; units: OrgUnit[] }) {
  const t = useTranslations("hr_integration.lifecycle");
  const format = useFormatter();
  const locale = useLocale();
  const [codes, setCodes] = useState(initialCodes);
  const [changes, setChanges] = useState(initialChanges);
  const [status, setStatus] = useState<string>("pending");
  const [chosen, setChosen] = useState<Record<string, string>>(() => Object.fromEntries(initialCodes.map((c) => [c.code, c.unitId ?? ""])));
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<Result>(null);

  const active = units.filter((u) => u.status === "active");
  const shown = changes.filter((c) => (status === "pending" ? c.status === "pending" || c.status === "unmapped" : c.status === status));
  const day = (d: string) => format.dateTime(new Date(`${d}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
  const when = (at: string) => format.dateTime(new Date(at), { dateStyle: "medium", timeStyle: "short" });

  async function reload() {
    const [c, h] = await Promise.all([fetch(`${BASE}/org-codes`, { cache: "no-store" }), fetch(`${BASE}/changes`, { cache: "no-store" })]);
    if (c.ok) setCodes((await c.json()) as HrOrgCode[]);
    if (h.ok) setChanges((await h.json()) as HrChange[]);
  }

  async function map(code: string) {
    setBusy(code);
    setResult(null);
    try {
      const response = await fetch(`${BASE}/org-codes/${encodeURIComponent(code)}`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ unitId: chosen[code] || null }),
      });
      if (response.ok) {
        await reload();
        setResult({ ok: true, messages: [t("codes.saved", { code })] });
      } else {
        const messages = await problemMessages(response);
        setResult({ ok: false, messages: messages.length > 0 ? messages : [t("failed")] });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(null);
    }
  }

  const describe = (c: HrChange) => {
    if (c.kind === "assignment") {
      const to = c.details.org_unit_code;
      return to ? t("details.assignmentTo", { code: to }) : t("details.assignment");
    }
    if (c.kind === "termination") {
      return t(c.details.immediate === "true" ? "details.immediate" : "details.endOfDay");
    }
    return null;
  };

  return (
    <>
      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}

      <section className="hr-integration-section" aria-labelledby="hr-codes-title">
        <h2 id="hr-codes-title">{t("codes.title")}</h2>
        <p>{t("codes.intro")}</p>
        {codes.length === 0 ? (
          <p className="notice">{t("codes.empty")}</p>
        ) : (
          <div className="hr-integration-table">
            <table>
              <caption className="visually-hidden">{t("codes.title")}</caption>
              <thead>
                <tr>
                  <th scope="col">{t("codes.code")}</th>
                  <th scope="col">{t("codes.people")}</th>
                  <th scope="col">{t("codes.unit")}</th>
                </tr>
              </thead>
              <tbody>
                {codes.map((c) => (
                  <tr key={c.code}>
                    <th scope="row">
                      <bdi dir="ltr">{c.code}</bdi>
                      {c.unitId ? null : <span className="hr-integration-meta">{t("codes.proposed")}</span>}
                      {c.waiting > 0 ? <span className="hr-integration-meta">{t("codes.waiting", { count: c.waiting })}</span> : null}
                    </th>
                    <td data-number>{format.number(c.people)}</td>
                    <td>
                      <div className="hr-integration-row">
                        <div className="field">
                          <label htmlFor={`hr-code-${c.code}`}>{t("codes.unitFor", { code: c.code })}</label>
                          <select id={`hr-code-${c.code}`} value={chosen[c.code] ?? ""} onChange={(e) => setChosen((m) => ({ ...m, [c.code]: e.target.value }))}>
                            <option value="">{t("codes.none")}</option>
                            {active.map((u) => (
                              <option key={u.id} value={u.id}>
                                {unitName(u, locale)}
                              </option>
                            ))}
                          </select>
                        </div>
                        <Button
                          variant="secondary"
                          disabled={(chosen[c.code] ?? "") === (c.unitId ?? "")}
                          busy={busy === c.code}
                          busyLabel={t("working")}
                          onClick={() => void map(c.code)}
                        >
                          {t("codes.save")}
                        </Button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="hr-integration-section" aria-labelledby="hr-changes-title">
        <h2 id="hr-changes-title">{t("changes.title")}</h2>
        <p>{t("changes.intro")}</p>
        <SegmentedControl
          label={t("changes.show")}
          name="hr-change-status"
          value={status}
          options={HR_CHANGE_STATUSES.filter((s) => s !== "unmapped").map((s) => ({ value: s, label: t(`statuses.${s}`) }))}
          onChange={setStatus}
        />
        {shown.length === 0 ? (
          <p className="notice">{t("changes.empty")}</p>
        ) : (
          <div className="hr-integration-table">
            <table>
              <caption className="visually-hidden">{t(`statuses.${status}`)}</caption>
              <thead>
                <tr>
                  <th scope="col">{t("changes.person")}</th>
                  <th scope="col">{t("changes.kind")}</th>
                  <th scope="col">{t("changes.effective")}</th>
                  <th scope="col">{t("changes.state")}</th>
                </tr>
              </thead>
              <tbody>
                {shown.map((c) => (
                  <tr key={c.id}>
                    <th scope="row">
                      {c.userName ? <bdi>{c.userName}</bdi> : t("changes.noPerson")}
                      <span className="hr-integration-meta">
                        <bdi dir="ltr">{c.recordId}</bdi>
                      </span>
                    </th>
                    <td>
                      {t(`kinds.${known(HR_CHANGE_KINDS, c.kind) ? c.kind : "other"}`)}
                      {describe(c) ? <span className="hr-integration-meta">{describe(c)}</span> : null}
                    </td>
                    <td>{day(c.effectiveDate)}</td>
                    <td>
                      {t(`statuses.${known(HR_CHANGE_STATUSES, c.status) ? c.status : "pending"}`)}
                      <span className="hr-integration-meta">
                        {c.status === "applied" && c.appliedAt
                          ? t("changes.appliedAt", { at: when(c.appliedAt) })
                          : c.status === "cancelled"
                            ? t(`reasons.${known(HR_CANCEL_REASONS, c.cancelledReason) ? c.cancelledReason : "other"}`)
                            : c.status === "unmapped"
                              ? t("changes.waitingForCode")
                              : t("changes.dueAt", { at: when(c.dueAt) })}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </>
  );
}
