"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import type { KbTemplate } from "@/lib/documents/types";
import "./documents.css";

/**
 * The new article form: a template and a title in each language the tenant uses. The API starts the draft with the
 * template's sections and refuses people who are not knowledge editors; the content editor takes it from there.
 */
export function NewArticleForm({
  templates,
  locales,
  defaultLocale,
}: {
  templates: KbTemplate[];
  locales: string[];
  defaultLocale: string;
}) {
  const t = useTranslations("documents.kb_new");
  const viewer = useLocale();
  const router = useRouter();
  const ids = useId();
  const [template, setTemplate] = useState<string>(templates[0]?.key ?? "sop");
  const [titles, setTitles] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const languages = new Intl.DisplayNames([viewer], { type: "language" });
  const ordered = [...locales].sort((a, b) =>
    a === viewer
      ? -1
      : b === viewer
        ? 1
        : a === defaultLocale
          ? -1
          : b === defaultLocale
            ? 1
            : 0,
  );

  async function create(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    try {
      const given = Object.fromEntries(
        Object.entries(titles).filter(([, v]) => v.trim()),
      );
      const response = await fetch("/api/v1/kb/articles", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ template, titles: given }),
      });
      if (!response.ok) {
        const found = await problemMessages(response);
        setMessages(
          found.length > 0
            ? found
            : [response.status === 403 ? t("denied") : t("failed")],
        );
        return;
      }
      const created = (await response.json()) as {
        id: string;
        editLink: string;
      };
      router.push(created.editLink);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  const chosen = templates.find((x) => x.key === template);
  return (
    <form
      className="documents-card"
      onSubmit={(e) => void create(e)}
      noValidate
    >
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <fieldset className="documents-templates">
        <legend>{t("template")}</legend>
        {templates.map((x) => (
          <label key={x.key} className="documents-check">
            <input
              type="radio"
              name={`${ids}-template`}
              value={x.key}
              checked={template === x.key}
              onChange={() => setTemplate(x.key)}
            />
            {t(`templateValue.${x.key}`)}
          </label>
        ))}
      </fieldset>
      <p className="field-hint" data-testid="template-sections">
        {chosen && chosen.sections.length > 0
          ? t("sections", {
              sections: chosen.sections
                .map((s) => t(`section.${s}`))
                .join(", "),
            })
          : t("faqHint")}
      </p>
      {ordered.map((l) => (
        <div className="field" key={l}>
          <label htmlFor={`${ids}-title-${l}`}>
            {t("titleIn", { language: languages.of(l) ?? l })}
          </label>
          <input
            id={`${ids}-title-${l}`}
            lang={l}
            dir="auto"
            value={titles[l] ?? ""}
            onChange={(e) => setTitles((x) => ({ ...x, [l]: e.target.value }))}
            maxLength={200}
          />
        </div>
      ))}
      <Button type="submit" variant="primary" busy={busy} busyLabel={t("busy")}>
        {t("create")}
      </Button>
    </form>
  );
}
