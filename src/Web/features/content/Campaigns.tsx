"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, SelectField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { campaignHref, pickName, variantFor, type CampaignView, type ItemView, type SiteView } from "@/lib/content/types";
import "./content.css";

function Problems({ messages }: { messages: string[] }) {
  return messages.length > 0 ? (
    <div className="notice notice-error" role="alert">
      {messages.map((m) => (
        <p key={m}>{m}</p>
      ))}
    </div>
  ) : null;
}

/** Sends a change and returns the campaign the API answered with, or shows its problems. */
function useSend() {
  const t = useTranslations("content.campaigns");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  async function send(url: string, method: "POST" | "PUT", body?: unknown): Promise<CampaignView | null> {
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(url, {
        method,
        headers: body === undefined ? undefined : { "content-type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (response.ok) return (await response.json()) as CampaignView;
      setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
    return null;
  }
  return { busy, messages, send };
}

/** Starting a campaign (spec 009 US8): a name and the site whose items it sends. Its audience starts as the site's. */
export function CampaignCreateForm({ sites }: { sites: SiteView[] }) {
  const t = useTranslations("content.campaigns");
  const locale = useLocale();
  const router = useRouter();
  const active = sites.filter((s) => s.status === "active");
  const [name, setName] = useState("");
  const [site, setSite] = useState(active.find((s) => s.isDefault)?.id ?? active[0]?.id ?? "");
  const { busy, messages, send } = useSend();

  async function create(event: React.FormEvent) {
    event.preventDefault();
    const created = await send("/api/v1/content/campaigns", "POST", { name: name.trim(), siteId: site || null });
    if (created) router.push(campaignHref(created.id));
  }

  return (
    <section className="content-card" aria-labelledby="campaign-create">
      <h2 id="campaign-create">{t("create")}</h2>
      <p className="field-hint">{t("createIntro")}</p>
      <Problems messages={messages} />
      <form className="content-site-form" onSubmit={(e) => void create(e)}>
        <FormField label={t("name")} name="name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} />
        <SelectField
          label={t("site")}
          name="site"
          value={site}
          onChange={(e) => setSite(e.target.value)}
          options={active.map((s) => ({ value: s.id, label: pickName(s.names, locale) }))}
        />
        <p className="content-actions">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("saving")}>
            {t("createSubmit")}
          </Button>
        </p>
      </form>
    </section>
  );
}

type StepDraft = { itemId: string; at: string; mandatory: boolean; onlyNotActed: boolean };

const pad = (n: number) => String(n).padStart(2, "0");

/** A UTC instant as a `datetime-local` value in the browser's time zone. */
function localValue(iso: string) {
  const d = new Date(iso);
  return [d.getFullYear(), "-", pad(d.getMonth() + 1), "-", pad(d.getDate()), "T", pad(d.getHours()), ":", pad(d.getMinutes())].join("");
}

/**
 * A campaign's plan (spec 009 US8 scenarios 1 and 3): its steps in time order, each an item of the site, a time, whether
 * it is mandatory (sent whatever people's preferences and labelled) and whether it goes only to people who have not
 * acted yet. The plan changes while the campaign is a draft; then it is started, and can be cancelled until it ends.
 */
export function CampaignPlan({ campaign, items }: { campaign: CampaignView; items: ItemView[] }) {
  const t = useTranslations("content.campaigns");
  const locale = useLocale();
  const router = useRouter();
  const draft = campaign.status === "draft";
  const [name, setName] = useState(campaign.name);
  const [steps, setSteps] = useState<StepDraft[]>(
    campaign.steps.map((s) => ({ itemId: s.itemId, at: localValue(s.at), mandatory: s.mandatory, onlyNotActed: s.onlyNotActed })),
  );
  const [saved, setSaved] = useState(false);
  const { busy, messages, send } = useSend();
  const url = `/api/v1/content/campaigns/${encodeURIComponent(campaign.id)}`;
  const options = items.map((i) => ({ value: i.id, label: variantFor(i, locale)?.title ?? i.id }));
  const blank = (): StepDraft => ({ itemId: items[0]?.id ?? "", at: localValue(new Date(Date.now() + 3600_000).toISOString()), mandatory: false, onlyNotActed: false });

  const change = (index: number, next: Partial<StepDraft>) => {
    setSaved(false);
    setSteps(steps.map((s, i) => (i === index ? { ...s, ...next } : s)));
  };

  async function save(event: React.FormEvent) {
    event.preventDefault();
    setSaved(false);
    const done = await send(url, "PUT", {
      version: campaign.version,
      name: name.trim(),
      steps: steps.map((s) => ({ itemId: s.itemId, at: new Date(s.at).toISOString(), mandatory: s.mandatory, onlyNotActed: s.onlyNotActed })),
    });
    if (done) {
      setSaved(true);
      router.refresh();
    }
  }

  async function act(action: "start" | "cancel") {
    if (await send(`${url}/${action}`, "POST")) router.refresh();
  }

  return (
    <section className="content-card" aria-labelledby="campaign-plan">
      <h2 id="campaign-plan">{t("plan")}</h2>
      <Problems messages={messages} />
      {saved ? (
        <p className="notice notice-success" role="status">
          {t("saved")}
        </p>
      ) : null}
      {draft ? (
        <form className="content-site-form" onSubmit={(e) => void save(e)}>
          <FormField label={t("name")} name="name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} />
          {steps.length === 0 ? <p className="field-hint">{t("noSteps")}</p> : null}
          {steps.map((s, index) => {
            const step = String(index + 1);
            return (
              <fieldset key={step} className="content-route">
                <legend>{t("step", { step })}</legend>
                <SelectField label={t("item")} name={`item-${step}`} value={s.itemId} onChange={(e) => change(index, { itemId: e.target.value })} options={options} />
                <FormField label={t("at")} name={`at-${step}`} type="datetime-local" value={s.at} onChange={(e) => change(index, { at: e.target.value })} required />
                <Checkbox label={t("mandatory")} checked={s.mandatory} onChange={(e) => change(index, { mandatory: e.target.checked })} />
                <Checkbox label={t("onlyNotActed")} checked={s.onlyNotActed} onChange={(e) => change(index, { onlyNotActed: e.target.checked })} />
                <Button variant="quiet" onClick={() => setSteps(steps.filter((_, i) => i !== index))}>
                  {t("removeStep", { step })}
                </Button>
              </fieldset>
            );
          })}
          {items.length === 0 ? <p className="field-hint">{t("noItems")}</p> : null}
          <p className="content-actions">
            {items.length > 0 ? <Button onClick={() => setSteps([...steps, blank()])}>{t("addStep")}</Button> : null}
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("saving")}>
              {t("save")}
            </Button>
          </p>
        </form>
      ) : (
        <ol className="content-list">
          {campaign.steps.map((s) => {
            const title = pickName(s.itemTitle, locale);
            const when = new Date(s.at).toLocaleString(locale);
            return (
              <li key={s.id} data-testid="campaign-step">
                <bdi>{title}</bdi> <span className="content-meta">{when}</span> <span className="content-meta">{t(`stepStatus.${s.status}`)}</span>
                {s.mandatory ? <span className="content-meta">{t("mandatoryLabel")}</span> : null}
              </li>
            );
          })}
        </ol>
      )}
      <p className="content-actions">
        {draft ? (
          <Button busy={busy} busyLabel={t("saving")} onClick={() => void act("start")}>
            {t("start")}
          </Button>
        ) : null}
        {campaign.status === "draft" || campaign.status === "scheduled" ? (
          <ConfirmAction
            label={t("cancel")}
            question={t("cancelQuestion", { name: campaign.name })}
            confirmLabel={t("cancelConfirm")}
            cancelLabel={t("keep")}
            onConfirm={() => act("cancel")}
          />
        ) : null}
      </p>
    </section>
  );
}
