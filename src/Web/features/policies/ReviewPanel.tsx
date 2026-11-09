"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import type { ClauseChange, CommentView, CompareView } from "@/lib/policies/types";
import { Button } from "@/components/ui/Button";
import { BodyView } from "@/features/content/BodyView";
import { TaskDecisionPanel } from "@/features/services/TaskDecisionPanel";
import "./policies.css";

/**
 * Reviewing a version (spec 025 US1 scenarios 1 and 2): each clause marked added, changed, unchanged or removed against
 * the version in force, with the earlier text a click away, comments on any clause, and the approval decision when the
 * person holds the task. Editors resolve comments.
 */
export function ReviewPanel({
  policyId,
  compare,
  initialComments,
  locale,
  taskId,
  canResolve,
}: {
  policyId: string;
  compare: CompareView;
  initialComments: CommentView[];
  locale: string;
  taskId: string | null;
  canResolve: boolean;
}) {
  const t = useTranslations("policies.review");
  const format = useFormatter();
  const router = useRouter();
  const ids = useId();
  const [comments, setComments] = useState(initialComments);
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<Record<string, string[]>>({});
  const base = `/api/v1/policies/${encodeURIComponent(policyId)}/drafts/${encodeURIComponent(compare.versionId)}/comments`;

  async function post(key: string, url: string, body: unknown, apply: (c: CommentView) => void) {
    setBusy(key);
    setMessages((m) => ({ ...m, [key]: [] }));
    try {
      const response = await fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
      if (!response.ok) {
        const problems = await problemMessages(response);
        setMessages((m) => ({ ...m, [key]: problems }));
        return;
      }
      apply((await response.json()) as CommentView);
    } catch {
      setMessages((m) => ({ ...m, [key]: [t("failed")] }));
    } finally {
      setBusy(null);
    }
  }

  const view = (c: ClauseChange) => c.after ?? c.before!;
  return (
    <div className="policies-page">
      <p className="policies-meta">{compare.base ? t("against", { number: compare.base }) : t("first")}</p>
      {compare.titlesChanged ? <p className="policies-banner">{t("titleChanged")}</p> : null}
      <ol className="policies-list" aria-label={t("clauses")}>
        {compare.clauses.map((c) => {
          const clause = view(c);
          const mine = comments.filter((x) => x.clauseKey === c.key);
          const headingId = `${ids}-${c.key}`;
          return (
            <li key={c.key} className="policies-card" aria-labelledby={headingId} data-testid="review-clause">
              <h2 id={headingId}>
                <span className="policies-number" dir="ltr">
                  {clause.number}
                </span>
                <bdi>{clause.titles[locale] ?? Object.values(clause.titles)[0] ?? ""}</bdi>{" "}
                <span className="policies-change" data-change={c.change}>
                  {t(`change.${c.change}`)}
                </span>
              </h2>
              {c.after?.bodies[locale] ? <BodyView body={c.after.bodies[locale]} embedTitle={clause.titles[locale] ?? ""} /> : null}
              {c.change !== "unchanged" && c.before?.bodies[locale] ? (
                <details>
                  <summary>{t("before", { number: compare.base ?? 0 })}</summary>
                  <BodyView body={c.before.bodies[locale]} embedTitle={clause.titles[locale] ?? ""} />
                </details>
              ) : null}
              {mine.length > 0 ? (
                <ul className="policies-list" aria-label={t("comments")}>
                  {mine.map((x) => (
                    <li key={x.id}>
                      <p>
                        <bdi>{x.author.displayName}</bdi>
                        {" · "}
                        <span className="policies-meta">{format.dateTime(new Date(x.createdAt), { dateStyle: "medium", timeStyle: "short" })}</span>
                      </p>
                      <p>
                        <bdi>{x.text}</bdi>
                      </p>
                      {x.resolvedAt ? (
                        <p className="policies-meta">{t("resolvedBy", { name: x.resolvedBy?.displayName ?? "" })}</p>
                      ) : canResolve ? (
                        <Button
                          variant="quiet"
                          busy={busy === x.id}
                          busyLabel={t("busy")}
                          onClick={() => void post(x.id, `${base}/${encodeURIComponent(x.id)}/resolve`, {}, (r) => setComments((all) => all.map((y) => (y.id === r.id ? r : y))))}
                        >
                          {t("resolve")}
                        </Button>
                      ) : null}
                    </li>
                  ))}
                </ul>
              ) : null}
              {c.change !== "removed" ? (
                <form
                  className="policies-page"
                  noValidate
                  onSubmit={(e) => {
                    e.preventDefault();
                    void post(c.key, base, { clauseKey: c.key, text: (drafts[c.key] ?? "").trim() }, (r) => {
                      setComments((all) => [...all, r]);
                      setDrafts((d) => ({ ...d, [c.key]: "" }));
                    });
                  }}
                >
                  <div className="field">
                    <label htmlFor={`${headingId}-comment`}>{t("comment", { number: clause.number })}</label>
                    <textarea
                      id={`${headingId}-comment`}
                      rows={2}
                      maxLength={2000}
                      dir="auto"
                      value={drafts[c.key] ?? ""}
                      onChange={(e) => setDrafts((d) => ({ ...d, [c.key]: e.target.value }))}
                    />
                  </div>
                  {(messages[c.key] ?? []).length > 0 ? (
                    <div className="notice notice-error" role="alert">
                      {messages[c.key]!.map((m) => (
                        <p key={m}>{m}</p>
                      ))}
                    </div>
                  ) : null}
                  <div className="button-row">
                    <Button type="submit" busy={busy === c.key} busyLabel={t("busy")}>
                      {t("addComment")}
                    </Button>
                  </div>
                </form>
              ) : null}
            </li>
          );
        })}
      </ol>
      {taskId ? <TaskDecisionPanel taskId={taskId} title={t("decision")} onDone={() => router.push("/tasks")} /> : null}
    </div>
  );
}
