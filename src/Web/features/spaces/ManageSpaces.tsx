"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { call, send, spacesApi } from "@/lib/spaces";
import "./spaces.css";

export type SpaceRules = { creationUnits: string[]; guestsAllowed: boolean; guestDays: number; version: number; guestConfidential: boolean };
export type TemplateView = { id: string; name: string; kind: "project" | "team"; status: "active" | "retired"; version: number; updatedAt: string };

/**
 * The organization's space rules and templates (spec 029 FR-012, FR-017) for spaces administrators: whether guests
 * may be invited and for how long, and which tenant templates creators can pick. The API validates every value.
 */
export function ManageSpaces({ initialRules, initialTemplates, guestsInPlan }: { initialRules: SpaceRules; initialTemplates: TemplateView[]; guestsInPlan: boolean }) {
  const t = useTranslations("spaces.manage");
  const [rules, setRules] = useState(initialRules);
  const [guests, setGuests] = useState(initialRules.guestsAllowed);
  const [days, setDays] = useState(String(initialRules.guestDays));
  const [confidential, setConfidential] = useState(initialRules.guestConfidential);
  const [templates, setTemplates] = useState(initialTemplates);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  async function save() {
    setSaved(false);
    const result = await call<SpaceRules>(
      () => send(`${spacesApi}/manage/settings`, "PUT", {
        creationUnits: rules.creationUnits, guestsAllowed: guests, guestDays: Number(days), guestConfidential: confidential, version: rules.version,
      }),
      t("failed"),
    );
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setRules(result.value);
    setSaved(true);
  }

  async function retire(template: TemplateView) {
    const result = await call<TemplateView>(() => send(`${spacesApi}/manage/templates/${template.id}:retire`, "POST"), t("failed"));
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setTemplates((list) => list.map((x) => (x.id === template.id ? result.value : x)));
  }

  return (
    <div className="spaces">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {saved ? <Alert tone="success" title={t("saved")} live /> : null}
      <section className="spaces-section" aria-labelledby="spaces-rules">
        <h2 id="spaces-rules">{t("rulesTitle")}</h2>
        <p className="spaces-meta">{rules.creationUnits.length === 0 ? t("everyoneCreates") : t("unitsCreate", { count: rules.creationUnits.length })}</p>
        <form
          className="spaces-form"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <Checkbox label={t("guests")} hint={guestsInPlan ? t("guestsHint") : t("guestsNotInPlan")} checked={guests} disabled={!guestsInPlan}
            onChange={(e) => setGuests(e.target.checked)} />
          <FormField label={t("guestDays")} hint={t("guestDaysHint")} name="guestDays" type="number" min={7} max={365} value={days} onChange={(e) => setDays(e.target.value)} />
          <Checkbox label={t("guestConfidential")} hint={t("guestConfidentialHint")} checked={confidential} disabled={!guestsInPlan}
            onChange={(e) => setConfidential(e.target.checked)} />
          <div className="spaces-row">
            <Button type="submit" variant="primary">
              {t("save")}
            </Button>
          </div>
        </form>
      </section>
      <section className="spaces-section" aria-labelledby="spaces-templates">
        <h2 id="spaces-templates">{t("templatesTitle")}</h2>
        <p className="spaces-meta">{t("templatesHint")}</p>
        {templates.length === 0 ? (
          <StateView kind="empty" title={t("noTemplatesTitle")} message={t("noTemplates")} icon="folder" />
        ) : (
          <ul className="spaces-list" aria-label={t("templatesTitle")}>
            {templates.map((template) => (
              <li key={template.id} className="spaces-row">
                <bdi>{template.name}</bdi>
                <Badge>{t(`kind.${template.kind}`)}</Badge>
                {template.status === "retired" ? (
                  <Badge tone="neutral">{t("retired")}</Badge>
                ) : (
                  <Button size="sm" variant="quiet" onClick={() => void retire(template)}>
                    {t("retire")}
                  </Button>
                )}
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
