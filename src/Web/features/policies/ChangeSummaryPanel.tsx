"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import type { ChangeSummaryView } from "@/lib/policies/types";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import "./policies.css";

/**
 * The AI change summary beside the compare view (spec 025 section 11): what the version in review changes against the
 * version in force, in plain words, labelled as AI-generated. The API keeps one per version and language, so the button
 * shows only until someone asks; the compare view below stays the record of what changed.
 */
export function ChangeSummaryPanel({ policyId, initial }: { policyId: string; initial: ChangeSummaryView }) {
  const t = useTranslations("policies.review.summary");
  const ids = useId();
  const [view, setView] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (view.outcome === "nothing_to_compare") {
    return null;
  }

  async function run() {
    setBusy(true);
    setError(null);
    try {
      const response = await fetch(`/api/v1/policies/${encodeURIComponent(policyId)}/compare/summary`, { method: "POST" });
      if (!response.ok) {
        setError((await problemMessages(response))[0] ?? t("failed"));
        return;
      }
      setView((await response.json()) as ChangeSummaryView);
    } catch {
      setError(t("failed"));
    } finally {
      setBusy(false);
    }
  }

  const denial = view.outcome === "policy_blocked" ? t("denial.classification_blocked") : view.denial ? (t.has(`denial.${view.denial}`) ? t(`denial.${view.denial}`) : t("denial.other")) : null;
  const s = view.summary;
  return (
    <section className="policies-card" aria-labelledby={`${ids}-title`} data-testid="change-summary" aria-busy={busy}>
      <h2 id={`${ids}-title`}>{t("title")}</h2>
      {s ? (
        <div lang={s.locale}>
          <Badge tone="accent" icon="spark">
            {t("aiGenerated")}
          </Badge>
          <p>
            <bdi>{s.summary}</bdi>
          </p>
          {s.points.length > 0 ? (
            <ul className="policies-list" aria-label={t("points")}>
              {s.points.map((p) => (
                <li key={p}>
                  <bdi>{p}</bdi>
                </li>
              ))}
            </ul>
          ) : null}
          <p className="policies-meta">{t("check")}</p>
        </div>
      ) : (
        <>
          <p className="policies-meta">{t("intro")}</p>
          {denial ? (
            <Alert tone="warning" title={t("none")}>
              <p>{denial}</p>
            </Alert>
          ) : null}
          {error ? <Alert tone="danger" title={error} /> : null}
          <div className="button-row">
            <Button icon="spark" onClick={() => void run()} busy={busy} busyLabel={t("busy")}>
              {t("action")}
            </Button>
          </div>
        </>
      )}
    </section>
  );
}
