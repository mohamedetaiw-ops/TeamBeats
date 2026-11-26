"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { nameIn, type ImpactView } from "@/lib/policies/types";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FactList } from "@/components/ui/Patterns";
import "./policies.css";

/**
 * What a change touches (spec 025 R3 US9), beside the change summary on the review page: the facts from the register
 * (changed clauses, people who attested the version in force, open exemptions, questions on changed clauses, linked
 * controls, related policies, onboarding) and, when asked, the AI assessment of which controls and policies each
 * changed clause may reach, labelled as AI-generated. The API keeps one assessment per version and language.
 */
export function ImpactPanel({ policyId, initial }: { policyId: string; initial: ImpactView }) {
  const t = useTranslations("policies.review.impact");
  const locale = useLocale();
  const ids = useId();
  const [view, setView] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const f = view.facts;
  if (view.outcome === "nothing_to_compare" || !f) {
    return null;
  }

  async function run() {
    setBusy(true);
    setError(null);
    try {
      const response = await fetch(`/api/v1/policies/${encodeURIComponent(policyId)}/impact/assessment`, { method: "POST" });
      if (!response.ok) {
        setError((await problemMessages(response))[0] ?? t("failed"));
        return;
      }
      setView((await response.json()) as ImpactView);
    } catch {
      setError(t("failed"));
    } finally {
      setBusy(false);
    }
  }

  const controlName = (key: string) => {
    const c = f.controls.find((m) => `${m.framework}:${m.code}`.toLowerCase() === key.toLowerCase());
    const title = c?.titles ? nameIn(c.titles, locale) : "";
    return c ? (title ? t("controlName", { code: c.code, title }) : c.code) : key;
  };
  const policyName = (id: string) => {
    const p = f.related.find((r) => r.id === id);
    return p ? t("policyName", { code: p.code, name: nameIn(p.names, locale) }) : id;
  };
  const denial = view.outcome === "policy_blocked" ? t("denial.classification_blocked") : view.denial ? (t.has(`denial.${view.denial}`) ? t(`denial.${view.denial}`) : t("denial.other")) : null;
  const a = view.assessment;

  return (
    <section className="policies-card" aria-labelledby={`${ids}-title`} data-testid="change-impact" aria-busy={busy}>
      <h2 id={`${ids}-title`}>{t("title")}</h2>
      <FactList
        facts={[
          { key: "clauses", label: t("facts.clauses"), value: t("facts.clausesValue", { count: f.clauses.length }) },
          { key: "attested", label: t("facts.attested"), value: t("facts.attestedValue", { count: f.attestedCurrent }) },
          { key: "exemptions", label: t("facts.exemptions"), value: t("facts.exemptionsValue", { count: f.openExemptions }) },
          {
            key: "questions",
            label: t("facts.questions"),
            value: f.questions.length === 0 ? t("facts.none") : t("facts.questionsValue", { count: f.questions.length, clauses: [...new Set(f.questions.map((q) => q.clauseNumber))].join(", ") }),
          },
          { key: "controls", label: t("facts.controls"), value: f.controls.length === 0 ? t("facts.none") : <bdi>{f.controls.map((c) => c.code).join(", ")}</bdi> },
          {
            key: "related",
            label: t("facts.related"),
            value: f.related.length === 0 ? t("facts.none") : <bdi>{f.related.map((p) => t("policyName", { code: p.code, name: nameIn(p.names, locale) })).join(", ")}</bdi>,
          },
          { key: "onboarding", label: t("facts.onboarding"), value: f.onboardingRequired ? t("facts.yes") : t("facts.no") },
        ]}
      />
      {a ? (
        <div lang={a.locale} data-testid="impact-assessment">
          <Badge tone="accent" icon="spark">
            {t("aiGenerated")}
          </Badge>
          <p>
            <bdi>{a.summary}</bdi>
          </p>
          {a.impacts.length > 0 ? (
            <ul className="policies-list" aria-label={t("byClause")}>
              {a.impacts.map((i, n) => (
                <li key={`${i.clause}-${n}`}>
                  <strong>{t("clause", { number: i.clause })}</strong> <bdi>{i.reason}</bdi>
                  <span className="policies-meta">
                    {" "}
                    <bdi>{[...i.controls.map(controlName), ...i.policies.map(policyName)].join(", ")}</bdi>
                  </span>
                </li>
              ))}
            </ul>
          ) : (
            <p className="policies-meta">{t("noReach")}</p>
          )}
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
