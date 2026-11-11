"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { TaskDecisionPanel } from "@/features/services/TaskDecisionPanel";
import { problemMessages } from "@/lib/notifications/types";
import { pick } from "@/lib/services/types";
import { letterTone, type Letter } from "@/lib/letters";
import "./letters.css";

/**
 * One HR letter (spec 022 US7). The person downloads it once issued or withdraws it while it waits. HR, from the
 * inbox task, sees what the letter will say, types in what HR does not hold and decides. The actions shown are the
 * ones the API says this person may take (`rights`); the API checks each one again.
 */
export function LetterDetail({ initial, taskId }: { initial: Letter; taskId: string | null }) {
  const t = useTranslations("letters");
  const locale = useLocale();
  const format = useFormatter();
  const [letter, setLetter] = useState(initial);
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries((initial.fields ?? []).filter((f) => !f.fromHr).map((f) => [f.key, f.value ?? ""])),
  );
  const [busy, setBusy] = useState<"values" | "withdraw" | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function call(kind: "values" | "withdraw", request: () => Promise<Response>, done: string) {
    setBusy(kind);
    setNotice(null);
    setMessages([]);
    try {
      const response = await request();
      if (response.ok) {
        setLetter((await response.json()) as Letter);
        setNotice(done);
      } else {
        const found = await problemMessages(response);
        setMessages(found.length > 0 ? found : [t("failed")]);
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  async function reload() {
    const response = await fetch(`/api/v1/hr/letters/${letter.id}`, { cache: "no-store" });
    if (response.ok) setLetter((await response.json()) as Letter);
    setNotice(t("detail.decided"));
  }

  const fieldLabel = (key: string, names: Record<string, string> | null) => (names ? pick(names, locale, key) : t(`fields.${key}`));
  return (
    <div className="letters">
      <section className="letters-section" aria-labelledby="letter-summary">
        <h2 id="letter-summary">{pick(letter.typeNames, locale, letter.typeKey)}</h2>
        {notice ? (
          <p className="notice" role="status">
            {notice}
          </p>
        ) : null}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        <dl className="letters-facts">
          <div>
            <dt>{t("detail.reference")}</dt>
            <dd>
              <bdi dir="ltr">{letter.reference}</bdi>
            </dd>
          </div>
          <div>
            <dt>{t("detail.person")}</dt>
            <dd>
              <bdi>{letter.person.displayName}</bdi>
            </dd>
          </div>
          <div>
            <dt>{t("language")}</dt>
            <dd>{t(`languages.${letter.language}`)}</dd>
          </div>
          <div>
            <dt>{t("detail.status")}</dt>
            <dd>
              <Badge tone={letterTone[letter.status]}>{t(`statuses.${letter.status}`)}</Badge>
            </dd>
          </div>
          {letter.addressee ? (
            <div>
              <dt>{t("addressee")}</dt>
              <dd>
                <bdi>{letter.addressee}</bdi>
              </dd>
            </div>
          ) : null}
          {letter.issuedAt ? (
            <div>
              <dt>{t("detail.issuedAt")}</dt>
              <dd>{format.dateTime(new Date(letter.issuedAt), { dateStyle: "long" })}</dd>
            </div>
          ) : null}
        </dl>
        {letter.status === "approved" ? <p className="field-hint">{t("detail.printing")}</p> : null}
        <div className="letters-row">
          {letter.rights.download ? (
            <a className="letters-download" href={`/api/v1/hr/letters/${letter.id}/pdf`} download>
              {t("download")}
            </a>
          ) : null}
          {letter.rights.withdraw ? (
            <Button
              busy={busy === "withdraw"}
              busyLabel={t("sending")}
              onClick={() => void call("withdraw", () => fetch(`/api/v1/hr/letters/${letter.id}:withdraw`, { method: "POST" }), t("detail.withdrawn"))}
            >
              {t("detail.withdraw")}
            </Button>
          ) : null}
        </div>
      </section>

      {letter.fields ? (
        <section className="letters-section" aria-labelledby="letter-fields">
          <h2 id="letter-fields">{t("detail.values")}</h2>
          <p className="field-hint">{t("detail.valuesIntro")}</p>
          <dl className="letters-facts">
            {letter.fields
              .filter((f) => f.fromHr)
              .map((f) => (
                <div key={f.key}>
                  <dt>{fieldLabel(f.key, f.names)}</dt>
                  <dd>
                    <bdi>{f.value}</bdi>
                  </dd>
                </div>
              ))}
          </dl>
          {letter.fields
            .filter((f) => !f.fromHr)
            .map((f) => (
              <FormField
                key={f.key}
                label={fieldLabel(f.key, f.names)}
                name={`letter-value-${f.key}`}
                value={values[f.key] ?? ""}
                maxLength={200}
                readOnly={!letter.rights.editValues}
                hint={f.value === null ? t("detail.missing") : undefined}
                onChange={(e) => setValues((all) => ({ ...all, [f.key]: e.target.value }))}
              />
            ))}
          {letter.rights.editValues && letter.fields.some((f) => !f.fromHr) ? (
            <div className="letters-row">
              <Button
                busy={busy === "values"}
                busyLabel={t("sending")}
                onClick={() =>
                  void call(
                    "values",
                    () =>
                      fetch(`/api/v1/hr/letters/${letter.id}/values`, {
                        method: "PUT",
                        headers: { "content-type": "application/json" },
                        body: JSON.stringify({ values }),
                      }),
                    t("detail.valuesSaved"),
                  )
                }
              >
                {t("detail.saveValues")}
              </Button>
            </div>
          ) : null}
        </section>
      ) : null}
      {taskId && letter.rights.decide ? <TaskDecisionPanel taskId={taskId} title={t("detail.decide")} onDone={() => void reload()} /> : null}
    </div>
  );
}
