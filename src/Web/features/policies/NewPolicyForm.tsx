"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { editHref } from "@/lib/policies/types";
import { Button } from "@/components/ui/Button";
import { PersonPicker } from "@/features/services/PersonPicker";
import "./policies.css";

/**
 * A new register entry (spec 025 US1): ID, name in each language, the binding language, the review cycle and an
 * optional named approver. Without one, the compliance officers of the policy's scope approve it. The API validates
 * everything and the person goes on to write the first draft.
 */
export function NewPolicyForm({ locales }: { locales: string[] }) {
  const t = useTranslations("policies.create");
  const ids = useId();
  const router = useRouter();
  const [code, setCode] = useState("");
  const [names, setNames] = useState<Record<string, string>>({});
  const [binding, setBinding] = useState("");
  const [reviewMonths, setReviewMonths] = useState("12");
  const [approver, setApprover] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function create(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch("/api/v1/policies", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          code: code.trim(),
          names: Object.fromEntries(Object.entries(names).filter(([, v]) => v.trim())),
          bindingLocale: binding || null,
          reviewMonths: Number(reviewMonths) || null,
          route: approver ? [{ approvers: [approver] }] : null,
        }),
      });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      const created = (await response.json()) as { id: string };
      router.push(editHref(created.id));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="policies-card" onSubmit={(e) => void create(e)} noValidate>
      <div className="field">
        <label htmlFor={`${ids}-code`}>{t("code")}</label>
        <input id={`${ids}-code`} value={code} onChange={(e) => setCode(e.target.value)} maxLength={40} dir="ltr" required />
      </div>
      {locales.map((l) => (
        <div className="field" key={l}>
          <label htmlFor={`${ids}-name-${l}`}>{t("name", { language: t(`language.${l}`) })}</label>
          <input
            id={`${ids}-name-${l}`}
            lang={l}
            dir={l === "ar" ? "rtl" : "ltr"}
            value={names[l] ?? ""}
            onChange={(e) => setNames((n) => ({ ...n, [l]: e.target.value }))}
            maxLength={300}
          />
        </div>
      ))}
      <div className="field">
        <label htmlFor={`${ids}-binding`}>{t("binding")}</label>
        <select id={`${ids}-binding`} value={binding} onChange={(e) => setBinding(e.target.value)}>
          <option value="">{t("noBinding")}</option>
          {locales.map((l) => (
            <option key={l} value={l}>
              {t(`language.${l}`)}
            </option>
          ))}
        </select>
      </div>
      <div className="field">
        <label htmlFor={`${ids}-review`}>{t("reviewMonths")}</label>
        <input id={`${ids}-review`} type="number" min={1} max={60} value={reviewMonths} onChange={(e) => setReviewMonths(e.target.value)} dir="ltr" />
      </div>
      <PersonPicker label={t("approver")} value={approver} onChange={setApprover} />
      <p className="policies-meta">{t("approverHint")}</p>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <div className="button-row">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("busy")}>
          {t("submit")}
        </Button>
      </div>
    </form>
  );
}
