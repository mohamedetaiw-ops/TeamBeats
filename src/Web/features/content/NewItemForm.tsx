"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { contentTypes, editHref, pickName, type ItemView, type SiteView } from "@/lib/content/types";
import "./content.css";

/** Starts an item (spec 009 US1): the site, the type and a title in the primary language; the rest is set in the editor. */
export function NewItemForm({ sites, locales, defaultLocale }: { sites: SiteView[]; locales: string[]; defaultLocale: string }) {
  const t = useTranslations("content.items");
  const viewer = useLocale();
  const router = useRouter();
  const ids = useId();
  const [site, setSite] = useState(sites.find((s) => s.isDefault)?.id ?? sites[0]?.id ?? "");
  const [type, setType] = useState<string>("news");
  const [primary, setPrimary] = useState(locales.includes(viewer) ? viewer : defaultLocale);
  const [title, setTitle] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function create(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch("/api/v1/content/items", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ siteId: site, type, variants: { [primary]: { title, body: { blocks: [] } } } }),
      });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      const item = (await response.json()) as ItemView;
      router.push(editHref(item.id));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="content-card" onSubmit={(e) => void create(e)} noValidate>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <div className="field">
        <label htmlFor={`${ids}-site`}>{t("site")}</label>
        <select id={`${ids}-site`} value={site} onChange={(e) => setSite(e.target.value)}>
          {sites.map((s) => (
            <option key={s.id} value={s.id}>
              {pickName(s.names, viewer)}
            </option>
          ))}
        </select>
      </div>
      <div className="field">
        <label htmlFor={`${ids}-type`}>{t("type")}</label>
        <select id={`${ids}-type`} value={type} onChange={(e) => setType(e.target.value)}>
          {contentTypes.map((c) => (
            <option key={c} value={c}>
              {t(`typeValue.${c}`)}
            </option>
          ))}
        </select>
      </div>
      <div className="field">
        <label htmlFor={`${ids}-primary`}>{t("primaryLanguage")}</label>
        <select id={`${ids}-primary`} value={primary} onChange={(e) => setPrimary(e.target.value)}>
          {locales.map((l) => (
            <option key={l} value={l}>
              {t(`language.${l === "ar" ? "ar" : "en"}`)}
            </option>
          ))}
        </select>
      </div>
      <FormField label={t("title")} name="title" required maxLength={200} lang={primary} dir={primary === "ar" ? "rtl" : "ltr"} value={title} onChange={(e) => setTitle(e.target.value)} />
      <div className="content-actions">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("creating")}>
          {t("create")}
        </Button>
      </div>
    </form>
  );
}
