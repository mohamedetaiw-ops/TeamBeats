"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import type { AgentReview } from "@/lib/agents/types";

type Result = { ok: boolean; messages: string[] } | null;

/** Shows a changed value briefly: text as it is, anything else as compact JSON. */
function show(value: unknown): string {
  if (value === null || value === undefined) return "";
  return typeof value === "string" ? value : JSON.stringify(value);
}

/**
 * The review of a version (spec 020 US2): what changed against the published version, the risk class and the gate
 * result, with approve and publish, or reject with a reason. The API refuses an approval by the last editor of a
 * Medium or High risk version and one the gate blocks; the page says why when approval is not possible. The gate
 * section lists failures by severity, failing cases and scores against the published version, and offers an override
 * with a reason when the API says the reader may give one (spec 034 US5).
 */
export function ReviewPanel({ review }: { review: AgentReview }) {
  const t = useTranslations("agents.review");
  const ts = useTranslations("agents");
  const router = useRouter();
  const ids = useId();
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<Result>(null);
  const [done, setDone] = useState(false);
  const [overrideReason, setOverrideReason] = useState("");
  const [overridden, setOverridden] = useState(false);

  async function decide(kind: "publish" | "reject") {
    setBusy(kind);
    setResult(null);
    try {
      const response = await fetch(`/api/v1/agents/${review.agent.id}/${kind}`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ version: review.version.number, reason: kind === "reject" ? reason : undefined }),
      });
      if (response.ok) {
        setDone(true);
        setResult({ ok: true, messages: [t(kind === "publish" ? "published" : "rejected")] });
        router.refresh();
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(null);
    }
  }

  async function override(runId: string) {
    setBusy("override");
    setResult(null);
    try {
      const response = await fetch(`/api/v1/evaluation/gates/${runId}/override`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ reason: overrideReason }),
      });
      if (response.ok) {
        setOverridden(true);
        setResult({ ok: true, messages: [t("overridden")] });
        router.refresh();
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(null);
    }
  }

  const gate = review.gate;
  const failures: [string, string[]][] = [
    ["critical", gate.criticalFailures ?? []],
    ["blocking", gate.blockingFailures ?? []],
    ["incomplete", gate.incomplete ?? []],
    ["cases", gate.failingCases ?? []],
  ];
  const diff = gate.diff ?? [];
  const score = (value: number | null) => (value === null ? "" : `${Math.round(value * 1000) / 10}%`);
  const cannot = (review.cannotApproveReason ?? "agent.publish_permission").replace("agent.", "");
  // A designer without agent.publish reads the review; deciding is for publishers, whom the API checks again.
  const mayDecide = review.canApprove || cannot !== "publish_permission";
  const field = (name: string) => (t.has(`fields.${name}`) ? t(`fields.${name}`) : name);
  return (
    <div className="agents">
      <p>
        <span className="agents-badge">{t("version", { number: String(review.version.number) })}</span>{" "}
        <span className="agents-badge" data-testid="review-risk">
          {ts(`risk.${review.version.riskClass}`)}
        </span>
      </p>
      <section className="agents-section" aria-labelledby={`${ids}-gate`}>
        <h2 id={`${ids}-gate`}>{t("gate")}</h2>
        <p data-testid="review-gate">{t.has(`gateStatus.${gate.status}`) ? t(`gateStatus.${gate.status}`) : gate.status}</p>
        {failures.map(([kind, list]) =>
          list.length > 0 ? (
            <div key={kind}>
              <h3>{t(`gateLists.${kind}`)}</h3>
              <ul data-testid={`review-gate-${kind}`}>
                {list.map((label) => (
                  <li key={label}>
                    <bdi dir="ltr">{label}</bdi>
                  </li>
                ))}
              </ul>
            </div>
          ) : null,
        )}
        {gate.overrideReason ? (
          <p data-testid="review-gate-override-reason">
            {t("overrideReasonShown")} <bdi>{gate.overrideReason}</bdi>
          </p>
        ) : null}
        {diff.length > 0 ? (
          <div className="agents-table">
            <table data-testid="review-gate-diff">
              <caption>{review.publishedVersion === null ? t("scores") : t("scoresAgainst", { number: String(review.publishedVersion) })}</caption>
              <thead>
                <tr>
                  <th scope="col">{t("metric")}</th>
                  <th scope="col">{t("locale")}</th>
                  <th scope="col">{t("publishedScore")}</th>
                  <th scope="col">{t("thisScore")}</th>
                </tr>
              </thead>
              <tbody>
                {diff.map((d) => (
                  <tr key={`${d.metric}:${d.locale}`}>
                    <th scope="row">
                      <bdi dir="ltr">{d.metric}</bdi>
                    </th>
                    <td>{d.locale}</td>
                    <td>{score(d.published)}</td>
                    <td>{score(d.value)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
        {gate.canOverride && gate.runId && !overridden ? (
          <div className="field">
            <label htmlFor={`${ids}-override`}>{t("overrideReason")}</label>
            <p id={`${ids}-override-hint`} className="field-hint">
              {t("overrideHint")}
            </p>
            <textarea
              id={`${ids}-override`}
              aria-describedby={`${ids}-override-hint`}
              rows={3}
              value={overrideReason}
              onChange={(e) => setOverrideReason(e.target.value)}
            />
            <div className="agents-row">
              <Button busy={busy === "override"} busyLabel={t("working")} onClick={() => void override(gate.runId!)}>
                {t("override")}
              </Button>
            </div>
          </div>
        ) : null}
      </section>
      <section className="agents-section" aria-labelledby={`${ids}-changes`}>
        <h2 id={`${ids}-changes`}>{review.publishedVersion === null ? t("firstVersion") : t("changes", { number: String(review.publishedVersion) })}</h2>
        <div className="agents-table">
          <table data-testid="review-changes">
            <thead>
              <tr>
                <th scope="col">{t("field")}</th>
                <th scope="col">{t("before")}</th>
                <th scope="col">{t("after")}</th>
              </tr>
            </thead>
            <tbody>
              {review.changes.map((c) => (
                <tr key={c.field}>
                  <th scope="row">{field(c.field)}</th>
                  <td>
                    <bdi>{show(c.from)}</bdi>
                  </td>
                  <td>
                    <bdi>{show(c.to)}</bdi>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
      {review.canApprove ? null : (
        <p className="notice" data-testid="review-cannot-approve">
          {t(`cannot.${cannot}`)}
        </p>
      )}
      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {done || !mayDecide ? null : (
        <section className="agents-section" aria-labelledby={`${ids}-decide`}>
          <h2 id={`${ids}-decide`}>{t("decide")}</h2>
          <div className="field">
            <label htmlFor={`${ids}-reason`}>{t("reason")}</label>
            <p id={`${ids}-reason-hint`} className="field-hint">
              {t("reasonHint")}
            </p>
            <textarea id={`${ids}-reason`} aria-describedby={`${ids}-reason-hint`} rows={3} value={reason} onChange={(e) => setReason(e.target.value)} />
          </div>
          <div className="agents-row">
            <Button variant="primary" disabled={!review.canApprove} busy={busy === "publish"} busyLabel={t("working")} onClick={() => void decide("publish")}>
              {t("approve")}
            </Button>
            <Button busy={busy === "reject"} busyLabel={t("working")} onClick={() => void decide("reject")}>
              {t("reject")}
            </Button>
          </div>
        </section>
      )}
    </div>
  );
}
