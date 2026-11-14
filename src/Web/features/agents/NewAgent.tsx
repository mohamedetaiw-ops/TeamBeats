"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { problemMessages } from "@/lib/notifications/types";
import type { OrgUnit } from "@/lib/experience/builder-types";
import { AGENT_CATEGORIES, pickText, type AgentDetail, type Localized, type StudioOffer } from "@/lib/agents/types";

const LANGUAGES = ["en", "ar"] as const;

/**
 * The first step of Agent Studio (spec 020 US4 scenario 1): a blank agent from its name, instructions, category and
 * languages, in the chosen org unit. Everything else starts at its safest value and is set in the editor. The API
 * checks the package, the custom agent limit and the designer's unit, and says who can raise the limit.
 */
export function NewAgent({ offer, units }: { offer: StudioOffer; units: OrgUnit[] }) {
  const t = useTranslations("agents.studio");
  const tc = useTranslations("agents.categories");
  const locale = useLocale();
  const router = useRouter();
  const ids = useId();
  const [unit, setUnit] = useState(units[0]?.id ?? "");
  const [name, setName] = useState<Localized>({});
  const [instructions, setInstructions] = useState<Localized>({});
  const [category, setCategory] = useState<string>("other");
  const [languages, setLanguages] = useState<string[]>(["en", "ar"]);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const full = offer.customLimit !== null && offer.customCount >= offer.customLimit;

  async function create() {
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch("/api/v1/agents", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ templateId: null, orgUnitId: unit, name, instructions, category, languages }),
      });
      if (response.ok) {
        const detail = (await response.json()) as AgentDetail;
        router.push(`/admin/agents/${detail.agent.id}`);
        return;
      }
      setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    }
    setBusy(false);
  }

  return (
    <section className="agents-section" aria-labelledby={`${ids}-title`} data-testid="agents-studio">
      <h2 id={`${ids}-title`}>{t("title")}</h2>
      <p>{t("intro")}</p>
      <p className="field-hint">
        {offer.customLimit === null ? t("usedNoLimit", { count: offer.customCount }) : t("used", { count: offer.customCount, limit: offer.customLimit })}
      </p>
      {full ? <p className="notice">{t("full")}</p> : null}
      <div className="field">
        <label htmlFor={`${ids}-unit`}>{t("unit")}</label>
        <select id={`${ids}-unit`} value={unit} onChange={(e) => setUnit(e.target.value)}>
          {units.map((u) => (
            <option key={u.id} value={u.id}>
              {pickText(u.names, locale)}
            </option>
          ))}
        </select>
      </div>
      <fieldset className="agents-checks">
        <legend>{t("languages")}</legend>
        {LANGUAGES.map((lang) => (
          <Checkbox
            key={lang}
            label={t(`languageNames.${lang}`)}
            checked={languages.includes(lang)}
            onChange={(e) => setLanguages(e.target.checked ? [...languages.filter((l) => l !== lang), lang] : languages.filter((l) => l !== lang))}
          />
        ))}
      </fieldset>
      <div className="agents-pair">
        {LANGUAGES.filter((lang) => languages.includes(lang)).map((lang) => (
          <div className="field" key={lang}>
            <label htmlFor={`${ids}-name-${lang}`}>{t("name", { language: t(`languageNames.${lang}`) })}</label>
            <input
              id={`${ids}-name-${lang}`}
              type="text"
              lang={lang}
              dir={lang === "ar" ? "rtl" : "ltr"}
              maxLength={80}
              value={name[lang] ?? ""}
              onChange={(e) => setName({ ...name, [lang]: e.target.value })}
            />
          </div>
        ))}
      </div>
      <div className="agents-pair">
        {LANGUAGES.filter((lang) => languages.includes(lang)).map((lang) => (
          <div className="field" key={lang}>
            <label htmlFor={`${ids}-instructions-${lang}`}>{t("instructions", { language: t(`languageNames.${lang}`) })}</label>
            <textarea
              id={`${ids}-instructions-${lang}`}
              rows={5}
              lang={lang}
              dir={lang === "ar" ? "rtl" : "ltr"}
              maxLength={4000}
              value={instructions[lang] ?? ""}
              onChange={(e) => setInstructions({ ...instructions, [lang]: e.target.value })}
            />
          </div>
        ))}
      </div>
      <div className="field">
        <label htmlFor={`${ids}-category`}>{t("category")}</label>
        <select id={`${ids}-category`} value={category} onChange={(e) => setCategory(e.target.value)}>
          {AGENT_CATEGORIES.map((c) => (
            <option key={c} value={c}>
              {tc(c)}
            </option>
          ))}
        </select>
      </div>
      <div className="agents-row">
        <Button variant="primary" disabled={unit === "" || languages.length === 0} busy={busy} busyLabel={t("creating")} onClick={() => void create()}>
          {t("create")}
        </Button>
      </div>
      {messages.length > 0 ? (
        <div role="alert" className="notice notice-error">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
    </section>
  );
}
