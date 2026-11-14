"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { PersonPicker } from "@/features/services/PersonPicker";
import { problemMessages } from "@/lib/notifications/types";
import { contentTypes, maxRouteSteps, type ContentRoute, type SiteView } from "@/lib/content/types";
import "./content.css";

type Draft = { approval: boolean; steps: string[][] };

const toDraft = (route: ContentRoute | undefined): Draft => ({
  approval: route?.approval ?? false,
  steps: route?.steps?.length ? route.steps.map((s) => [...(s.approvers ?? [])]) : [[...(route?.approvers ?? [])]],
});

/** One step keeps the plain shape; more steps go as a list. The API decides whether the plan allows them. */
const toRoute = (draft: Draft): ContentRoute =>
  !draft.approval
    ? { approval: false }
    : draft.steps.length === 1
      ? { approval: true, approvers: draft.steps[0] }
      : { approval: true, steps: draft.steps.map((approvers) => ({ approvers })) };

/**
 * The approval route of each content type of a site (spec 009 FR-003): whether items need approval, and the steps in
 * order with the people who decide each one. A step with nobody named goes to the site's publishers.
 */
export function RouteSettings({ site }: { site: SiteView }) {
  const t = useTranslations("content.sites.routes");
  const types = useTranslations("content.items.typeValue");
  const router = useRouter();
  const [drafts, setDrafts] = useState<Record<string, Draft>>(() => Object.fromEntries(contentTypes.map((type) => [type, toDraft(site.routes?.[type])])));
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const archived = site.status === "archived";

  const change = (type: string, next: Draft) => {
    setSaved(false);
    setDrafts({ ...drafts, [type]: next });
  };

  async function save(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setSaved(false);
    setMessages([]);
    try {
      const routes = { ...(site.routes ?? {}), ...Object.fromEntries(Object.entries(drafts).map(([type, d]) => [type, toRoute(d)])) };
      const response = await fetch(`/api/v1/content/sites/${encodeURIComponent(site.id)}`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ version: site.version, routes }),
      });
      if (response.ok) {
        setSaved(true);
        router.refresh();
        return;
      }
      setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="content-card" aria-labelledby="site-routes">
      <h2 id="site-routes">{t("title")}</h2>
      <p className="field-hint">{t("intro")}</p>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {saved ? (
        <p className="notice notice-success" role="status">
          {t("saved")}
        </p>
      ) : null}
      <form className="content-site-form" onSubmit={(e) => void save(e)}>
        {contentTypes.map((type) => {
          const draft = drafts[type] ?? toDraft(undefined);
          const label = types(type);
          return (
            <fieldset key={type} className="content-route">
              <legend>{label}</legend>
              <Checkbox
                label={t("approval")}
                checked={draft.approval}
                disabled={archived}
                onChange={(e) => change(type, { ...draft, approval: e.target.checked })}
              />
              {draft.approval
                ? draft.steps.map((approvers, index) => {
                    const step = index + 1;
                    const setStep = (next: string[]) => change(type, { ...draft, steps: draft.steps.map((s, i) => (i === index ? next : s)) });
                    return (
                      <div key={step} className="content-route-step" role="group" aria-label={t("step", { step, type: label })}>
                        <h3>{t("step", { step, type: label })}</h3>
                        {approvers.length === 0 ? <p className="field-hint">{t("publishers")}</p> : null}
                        {approvers.map((id) => (
                          <PersonPicker
                            key={id}
                            label={t("approver")}
                            value={id}
                            onChange={(next) => setStep(next ? approvers.map((a) => (a === id ? next : a)) : approvers.filter((a) => a !== id))}
                          />
                        ))}
                        <PersonPicker
                          key={`add-${approvers.length}`}
                          label={t("addApprover")}
                          value={null}
                          onChange={(next) => (next && !approvers.includes(next) ? setStep([...approvers, next]) : undefined)}
                        />
                        {draft.steps.length > 1 ? (
                          <Button variant="quiet" onClick={() => change(type, { ...draft, steps: draft.steps.filter((_, i) => i !== index) })}>
                            {t("removeStep", { step })}
                          </Button>
                        ) : null}
                      </div>
                    );
                  })
                : null}
              {draft.approval && draft.steps.length < maxRouteSteps ? (
                <Button onClick={() => change(type, { ...draft, steps: [...draft.steps, []] })}>{t("addStep")}</Button>
              ) : null}
            </fieldset>
          );
        })}
        {archived ? null : (
          <p className="content-actions">
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("saving")}>
              {t("save")}
            </Button>
          </p>
        )}
      </form>
    </section>
  );
}
