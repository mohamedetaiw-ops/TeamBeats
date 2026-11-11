"use client";

import Link from "next/link";
import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField } from "@/components/ui/FormField";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { PersonPicker } from "@/features/services/PersonPicker";
import { HR_PROBLEMS, HR_STATES, known, type HrOverview, type HrRecord, type HrRecordPage, type HrRun } from "@/lib/integrations/types";
import { problemMessages } from "@/lib/notifications/types";
import "./hr-integration.css";

const BASE = "/api/v1/hr/integration";
const RULES = ["work_email,employee_number", "employee_number,work_email", "work_email", "employee_number"] as const;

type Result = { ok: boolean; messages: string[] } | null;

const post = (body?: unknown, etag?: string): RequestInit => ({
  method: "POST",
  headers: { "content-type": "application/json", ...(etag ? { "if-match": etag } : {}) },
  body: body === undefined ? undefined : JSON.stringify(body),
});

/**
 * The HR integration page (spec 019 section 10): the connection, counts and last run, a run held by the change
 * threshold to confirm or discard, the records by state with links made and removed by hand, the matching rule and
 * threshold, a sync now and the run history. The API decides and checks everything; this screen shows its answers.
 */
export function HrIntegrationAdmin({ initial, initialRuns, initialRecords }: { initial: HrOverview; initialRuns: HrRun[]; initialRecords: HrRecordPage }) {
  const t = useTranslations("hr_integration");
  const format = useFormatter();
  const locale = useLocale();
  const [overview, setOverview] = useState(initial);
  const [runs, setRuns] = useState(initialRuns);
  const [state, setState] = useState<string>("unmatched");
  const [search, setSearch] = useState("");
  const [records, setRecords] = useState(initialRecords);
  const [picked, setPicked] = useState<Record<string, string | null>>({});
  const [rule, setRule] = useState(initial.settings.matchRule.join(","));
  const [threshold, setThreshold] = useState(String(initial.settings.changeThresholdPercent));
  const [leadDays, setLeadDays] = useState(String(initial.settings.onboardingLeadDays));
  const [rehire, setRehire] = useState(initial.settings.rehireReactivates);
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<Result>(null);

  const when = (at: string | null) => (at ? format.dateTime(new Date(at), { dateStyle: "medium", timeStyle: "short" }) : t("never"));
  const etag = `"v${overview.settings.version}"`;

  async function refresh(nextState = state, nextSearch = search) {
    const query = new URLSearchParams({ state: nextState });
    if (nextSearch.trim()) query.set("search", nextSearch.trim());
    const [o, r, list] = await Promise.all([fetch(BASE, { cache: "no-store" }), fetch(`${BASE}/runs`, { cache: "no-store" }), fetch(`${BASE}/records?${query}`, { cache: "no-store" })]);
    if (o.ok) setOverview((await o.json()) as HrOverview);
    if (r.ok) setRuns((await r.json()) as HrRun[]);
    if (list.ok) setRecords((await list.json()) as HrRecordPage);
  }

  async function run(key: string, action: () => Promise<Response>, success: string) {
    setBusy(key);
    setResult(null);
    try {
      const response = await action();
      if (response.ok) {
        await refresh();
        setResult({ ok: true, messages: [success] });
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

  async function more() {
    if (!records.nextCursor) return;
    const query = new URLSearchParams({ state, cursor: records.nextCursor });
    if (search.trim()) query.set("search", search.trim());
    const response = await fetch(`${BASE}/records?${query}`, { cache: "no-store" });
    if (response.ok) {
      const page = (await response.json()) as HrRecordPage;
      setRecords({ items: [...records.items, ...page.items], nextCursor: page.nextCursor });
    }
  }

  const name = (r: HrRecord) => (locale === "ar" ? (r.nameAr ?? r.nameEn) : (r.nameEn ?? r.nameAr)) ?? r.recordId;
  const held = overview.heldRun;
  const last = overview.lastRun;

  return (
    <div className="hr-integration">
      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}

      <section className="hr-integration-section" aria-labelledby="hr-status-title">
        <h2 id="hr-status-title">{t("status.title")}</h2>
        {overview.connection ? (
          <p>
            {t("status.connection")}{" "}
            <Link href={`/admin/connections/${overview.connection.id}`}>
              <bdi>{overview.connection.displayName}</bdi>
            </Link>{" "}
            <span className="hr-integration-meta">{t(`kinds.${overview.connection.kind === "hr_sftp" ? "hr_sftp" : "hr_rest"}`)}</span>
          </p>
        ) : (
          <p className="notice">
            {t("status.noConnection")} <Link href="/admin/connections">{t("status.addConnection")}</Link>
          </p>
        )}
        <dl className="hr-integration-counts">
          {(["matched", "unmatched", "stale", "conflicts"] as const).map((k) => (
            <div key={k}>
              <dt>{t(`counts.${k}`)}</dt>
              <dd data-number>{format.number(overview.counts[k])}</dd>
            </div>
          ))}
        </dl>
        <p>{last ? t("status.lastRun", { at: when(last.finishedAt ?? last.startedAt), status: t(`runStatus.${last.status}`) }) : t("status.noRun")}</p>
        {overview.connection ? (
          <div className="hr-integration-row">
            <Button busy={busy === "sync"} busyLabel={t("working")} onClick={() => void run("sync", () => fetch(`${BASE}/runs`, post()), t("status.queued"))}>
              {t("status.syncNow")}
            </Button>
          </div>
        ) : null}
      </section>

      {held ? (
        <section className="hr-integration-section hr-integration-held" aria-labelledby="hr-held-title">
          <h2 id="hr-held-title">{t("held.title")}</h2>
          <p>
            {t("held.intro", {
              terminations: format.number(held.terminations),
              moves: format.number(held.moves),
              active: format.number(held.activeBefore),
              threshold: format.number(overview.settings.changeThresholdPercent),
            })}
          </p>
          <p className="hr-integration-meta">{t("held.read", { at: when(held.startedAt), total: format.number(held.total) })}</p>
          <div className="hr-integration-row">
            <ConfirmAction
              label={t("held.confirm")}
              question={t("held.confirmQuestion")}
              confirmLabel={t("held.confirmYes")}
              cancelLabel={t("cancel")}
              onConfirm={() => run("confirm", () => fetch(`${BASE}/runs/${held.id}:confirm`, post()), t("held.confirmed"))}
            />
            <Button variant="secondary" busy={busy === "discard"} busyLabel={t("working")} onClick={() => void run("discard", () => fetch(`${BASE}/runs/${held.id}:discard`, post()), t("held.discarded"))}>
              {t("held.discard")}
            </Button>
          </div>
        </section>
      ) : null}

      <section className="hr-integration-section" aria-labelledby="hr-records-title">
        <h2 id="hr-records-title">{t("records.title")}</h2>
        <SegmentedControl
          label={t("records.state")}
          name="hr-state"
          value={state}
          options={HR_STATES.map((s) => ({ value: s, label: t(`states.${s}`) }))}
          onChange={(s) => {
            setState(s);
            void refresh(s);
          }}
        />
        <form
          className="hr-integration-row"
          role="search"
          onSubmit={(e) => {
            e.preventDefault();
            void refresh(state, search);
          }}
        >
          <FormField label={t("records.search")} hint={t("records.searchHint")} name="search" type="search" value={search} onChange={(e) => setSearch(e.target.value)} />
          <Button type="submit" variant="secondary">
            {t("records.find")}
          </Button>
        </form>
        {records.items.length === 0 ? (
          <p className="notice">{t("records.empty")}</p>
        ) : (
          <div className="hr-integration-table">
            <table>
              <caption>{t(`states.${known(HR_STATES, state) ? state : "unmatched"}`)}</caption>
              <thead>
                <tr>
                  <th scope="col">{t("records.record")}</th>
                  <th scope="col">{t("records.email")}</th>
                  <th scope="col">{t("records.number")}</th>
                  <th scope="col">{t("records.seen")}</th>
                  <th scope="col">{t("records.match")}</th>
                </tr>
              </thead>
              <tbody>
                {records.items.map((r) => (
                  <tr key={r.recordId}>
                    <th scope="row">
                      <bdi>{name(r)}</bdi>
                      <span className="hr-integration-meta">
                        <bdi dir="ltr">{r.recordId}</bdi>
                      </span>
                    </th>
                    <td>{r.workEmail ? <bdi dir="ltr">{r.workEmail}</bdi> : t("none")}</td>
                    <td>{r.employeeNumber ? <bdi dir="ltr">{r.employeeNumber}</bdi> : t("none")}</td>
                    <td>{t("records.seenRange", { first: when(r.firstSeenAt), last: when(r.lastSeenAt) })}</td>
                    <td>
                      {r.userId ? (
                        <div className="hr-integration-cell">
                          <bdi>{r.userName ?? r.userId}</bdi>
                          <span className="hr-integration-meta">{r.matchRule ? t(`rules.${r.matchRule}`) : null}</span>
                          <Button
                            variant="quiet"
                            busy={busy === `unlink-${r.recordId}`}
                            busyLabel={t("working")}
                            onClick={() => void run(`unlink-${r.recordId}`, () => fetch(`${BASE}/records/${encodeURIComponent(r.recordId)}:unlink`, post()), t("records.unlinked"))}
                          >
                            {t("records.unlink")}
                          </Button>
                        </div>
                      ) : (
                        <div className="hr-integration-cell">
                          <PersonPicker label={t("records.linkTo", { name: name(r) })} value={picked[r.recordId] ?? null} onChange={(id) => setPicked((m) => ({ ...m, [r.recordId]: id }))} />
                          <Button
                            variant="secondary"
                            disabled={!picked[r.recordId]}
                            busy={busy === `link-${r.recordId}`}
                            busyLabel={t("working")}
                            onClick={() =>
                              void run(`link-${r.recordId}`, () => fetch(`${BASE}/records/${encodeURIComponent(r.recordId)}:link`, post({ userId: picked[r.recordId] })), t("records.linked"))
                            }
                          >
                            {t("records.link")}
                          </Button>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {records.nextCursor ? (
          <div className="hr-integration-row">
            <Button variant="secondary" onClick={() => void more()}>
              {t("records.more")}
            </Button>
          </div>
        ) : null}
      </section>

      <section className="hr-integration-section" aria-labelledby="hr-settings-title">
        <h2 id="hr-settings-title">{t("settings.title")}</h2>
        <form
          className="hr-integration-form"
          onSubmit={(e) => {
            e.preventDefault();
            void run(
              "settings",
              () =>
                fetch(`${BASE}/settings`, {
                  ...post({ matchRule: rule.split(","), changeThresholdPercent: Number(threshold), onboardingLeadDays: Number(leadDays), rehireReactivates: rehire }, etag),
                  method: "PUT",
                }),
              t("settings.saved"),
            );
          }}
        >
          <div className="field">
            <label htmlFor="hr-match-rule">{t("settings.matchRule")}</label>
            <select id="hr-match-rule" value={rule} onChange={(e) => setRule(e.target.value)} aria-describedby="hr-match-rule-hint">
              {RULES.map((r) => (
                <option key={r} value={r}>
                  {t(`settings.ruleOptions.${r.replace(",", "_then_")}`)}
                </option>
              ))}
            </select>
            <p id="hr-match-rule-hint" className="field-hint">
              {t("settings.matchRuleHint")}
            </p>
          </div>
          <FormField
            label={t("settings.threshold")}
            hint={t("settings.thresholdHint")}
            name="changeThresholdPercent"
            type="number"
            min={1}
            max={100}
            required
            value={threshold}
            onChange={(e) => setThreshold(e.target.value)}
          />
          <FormField
            label={t("settings.leadDays")}
            hint={t("settings.leadDaysHint")}
            name="onboardingLeadDays"
            type="number"
            min={0}
            max={30}
            required
            value={leadDays}
            onChange={(e) => setLeadDays(e.target.value)}
          />
          <Checkbox label={t("settings.rehire")} hint={t("settings.rehireHint")} checked={rehire} onChange={(e) => setRehire(e.target.checked)} />
          <div className="hr-integration-row">
            <Button type="submit" variant="primary" busy={busy === "settings"} busyLabel={t("working")}>
              {t("settings.save")}
            </Button>
          </div>
        </form>
      </section>

      <section className="hr-integration-section" aria-labelledby="hr-runs-title">
        <h2 id="hr-runs-title">{t("runs.title")}</h2>
        {runs.length === 0 ? (
          <p className="notice">{t("status.noRun")}</p>
        ) : (
          <div className="hr-integration-table">
            <table>
              <caption className="visually-hidden">{t("runs.title")}</caption>
              <thead>
                <tr>
                  <th scope="col">{t("runs.started")}</th>
                  <th scope="col">{t("runs.status")}</th>
                  <th scope="col">{t("runs.total")}</th>
                  <th scope="col">{t("counts.matched")}</th>
                  <th scope="col">{t("counts.unmatched")}</th>
                  <th scope="col">{t("counts.conflicts")}</th>
                  <th scope="col">{t("runs.changed")}</th>
                  <th scope="col">{t("runs.refused")}</th>
                </tr>
              </thead>
              <tbody>
                {runs.map((r) => (
                  <tr key={r.id}>
                    <th scope="row">{when(r.startedAt)}</th>
                    <td>{t(`runStatus.${r.status}`)}</td>
                    <td data-number>{format.number(r.total)}</td>
                    <td data-number>{format.number(r.matched)}</td>
                    <td data-number>{format.number(r.unmatched)}</td>
                    <td data-number>{format.number(r.conflicts)}</td>
                    <td data-number>{format.number(r.changed)}</td>
                    <td>
                      {r.problems.length === 0 ? (
                        format.number(0)
                      ) : (
                        <ul className="hr-integration-problems">
                          {r.problems.slice(0, 20).map((p, i) => (
                            <li key={`${p.recordId ?? ""}-${i}`}>
                              {p.recordId ? <bdi dir="ltr">{p.recordId}</bdi> : t("runs.noId")} {t(`problems.${known(HR_PROBLEMS, p.reason) ? p.reason : "other"}`)}
                            </li>
                          ))}
                          {r.problems.length > 20 ? <li>{t("runs.moreProblems", { count: r.problems.length - 20 })}</li> : null}
                        </ul>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}
