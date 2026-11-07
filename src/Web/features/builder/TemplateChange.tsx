"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import type { CatalogueTemplate, PageView, TemplateChange as Change } from "@/lib/experience/builder-types";

/**
 * Change of template or layout on an existing page (spec 032 US5 scenarios 3, 6 and 7). The API works the change out
 * first: placements it cannot keep are listed here for the editor to put in another region or remove, and nothing is
 * saved until the editor applies it. Applying works on the saved draft, so the editor saves before changing.
 */
export function TemplateChange({ page, templates }: { page: PageView; templates: CatalogueTemplate[] }) {
  const t = useTranslations("templates.change");
  const tb = useTranslations("builder");
  const router = useRouter();
  const [open, setOpen] = useState(Boolean(page.newerTemplateVersion));
  const [templateId, setTemplateId] = useState(page.newerTemplateVersion ? page.templateId : (templates[0]?.id ?? ""));
  const chosen = templates.find((x) => x.id === templateId);
  const [variant, setVariant] = useState(page.draft.variant);
  const [assignments, setAssignments] = useState<Record<string, string>>({});
  const [plan, setPlan] = useState<Change | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const chosenVariant = chosen?.variants.includes(variant) ? variant : (chosen?.variants[0] ?? variant);

  async function call(apply: boolean, current = assignments) {
    setBusy(apply ? "apply" : "review");
    setMessage(null);
    try {
      const headers: Record<string, string> = { "content-type": "application/json" };
      if (apply) {
        // The latest saved draft, which may be newer than this screen after autosave.
        const latest = await fetch(`/api/v1/experience/pages/${page.id}`);
        headers["if-match"] = latest.headers.get("etag") ?? ((await latest.json()) as PageView).eTag;
      }
      const response = await fetch(`/api/v1/experience/pages/${page.id}/change-template`, {
        method: "POST",
        headers,
        body: JSON.stringify({ templateId, variant: chosenVariant, assignments: current, apply }),
      });
      if (response.ok) {
        const result = (await response.json()) as Change;
        if (apply) {
          setPlan(null);
          setMessage(t("applied"));
          router.refresh();
        } else {
          setPlan(result);
        }
        return;
      }
      const problem = (await response.json().catch(() => null)) as { title?: string; findings?: Change["findings"] } | null;
      if (response.status === 409) setMessage(t("changed"));
      else if (response.status === 422) setMessage(t("blocked"));
      else setMessage(problem?.title ?? t("failed"));
    } catch {
      setMessage(t("failed"));
    } finally {
      setBusy(null);
    }
  }

  function assign(placement: string, region: string) {
    const next = { ...assignments, [placement]: region };
    setAssignments(next);
    void call(false, next);
  }

  const blocking = plan?.findings.filter((f) => f.severity === "blocking").length ?? 0;
  return (
    <section className="builder-template-change" aria-labelledby="template-change-title">
      <h2 id="template-change-title">{t("title")}</h2>
      {page.newerTemplateVersion ? (
        <p className="notice" data-testid="template-newer">
          {t("newer", { version: page.newerTemplateVersion })}
        </p>
      ) : null}
      {page.templateDeprecated ? (
        <p className="notice" data-testid="template-deprecated">
          {t("deprecated")}
        </p>
      ) : null}
      {!open ? (
        <Button onClick={() => setOpen(true)}>{t("open")}</Button>
      ) : (
        <>
          <div className="field">
            <label htmlFor="change-template">{t("template")}</label>
            <select
              id="change-template"
              value={templateId}
              onChange={(e) => {
                setTemplateId(e.target.value);
                setPlan(null);
                setAssignments({});
              }}
            >
              {templates.map((x) => (
                <option key={x.id} value={x.id}>
                  {x.name}
                </option>
              ))}
            </select>
            {chosen ? <p className="field-hint">{chosen.description}</p> : null}
          </div>
          <div className="field">
            <label htmlFor="change-variant">{t("variant")}</label>
            <select id="change-variant" value={chosenVariant} onChange={(e) => setVariant(e.target.value)}>
              {(chosen?.variants ?? []).map((v) => (
                <option key={v} value={v}>
                  {tb(`variants.${v}`)}
                </option>
              ))}
            </select>
          </div>
          <div className="builder-actions">
            <Button onClick={() => void call(false)} busy={busy === "review"} busyLabel={t("reviewing")}>
              {t("review")}
            </Button>
          </div>
          {plan ? (
            <div data-testid="template-change-plan">
              {plan.unplaced.length === 0 ? (
                <p className="builder-template-description">{t("allKept")}</p>
              ) : (
                <>
                  <p className="builder-template-description">{t("unplacedIntro", { count: plan.unplaced.length })}</p>
                  <ul>
                    {plan.unplaced.map((u) => (
                      <li key={u.placementId} className="field">
                        <label htmlFor={`assign-${u.placementId}`}>
                          {t("placement", { placement: u.placementId, region: tb(`regions.${u.fromRegion}`) })}
                        </label>
                        <select id={`assign-${u.placementId}`} value={assignments[u.placementId] ?? "-"} onChange={(e) => assign(u.placementId, e.target.value)}>
                          <option value="-" disabled>
                            {t("choose")}
                          </option>
                          {u.options.map((o) => (
                            <option key={o} value={o}>
                              {tb(`regions.${o}`)}
                            </option>
                          ))}
                          <option value="">{t("remove")}</option>
                        </select>
                      </li>
                    ))}
                  </ul>
                </>
              )}
              {blocking > 0 ? <p className="builder-template-description">{t("fixFirst", { count: blocking })}</p> : null}
              <Button variant="primary" onClick={() => void call(true)} busy={busy === "apply"} busyLabel={t("applying")} disabled={blocking > 0}>
                {t("apply")}
              </Button>
            </div>
          ) : null}
        </>
      )}
      <p role="status" className="builder-template-description">
        {message}
      </p>
    </section>
  );
}
