"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { problemMessages } from "@/lib/notifications/types";
import { pick } from "@/lib/services/types";
import { letterTone, type Letter, type LetterChoice, type LetterLanguage } from "@/lib/letters";
import "./letters.css";

/**
 * Asking for an HR letter (spec 022 US7 scenario 1): the type, the language and who it is addressed to. A type that
 * needs approval waits for HR; one that does not is issued at once. The API decides which types apply to the person
 * and fills the letter from HR; nothing about the template reaches this page.
 */
export function MyLetters({ choices }: { choices: LetterChoice[] }) {
  const t = useTranslations("letters");
  const locale = useLocale();
  const format = useFormatter();
  const [type, setType] = useState(choices[0]?.key ?? "");
  const [language, setLanguage] = useState<LetterLanguage>(locale === "ar" ? "ar" : "en");
  const [addressee, setAddressee] = useState("");
  const [list, setList] = useState<Letter[] | null>(null);
  const [reload, setReload] = useState(0);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch("/api/v1/me/letters", { cache: "no-store" });
        if (live && response.ok) setList((await response.json()) as Letter[]);
      } catch {
        if (live) setList([]);
      }
    })();
    return () => {
      live = false;
    };
  }, [reload]);

  async function send() {
    setBusy(true);
    setNotice(null);
    setMessages([]);
    try {
      const response = await fetch("/api/v1/me/letters", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ type, language, addressee: addressee || null }),
      });
      if (!response.ok) {
        const found = await problemMessages(response);
        setMessages(found.length > 0 ? found : [t("failed")]);
        return;
      }
      const letter = (await response.json()) as Letter;
      setNotice(letter.status === "issued" ? t("issuedNow", { reference: letter.reference }) : t("sent", { reference: letter.reference }));
      setAddressee("");
      setReload((n) => n + 1);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  const chosen = choices.find((c) => c.key === type);
  return (
    <div className="letters">
      <section className="letters-section" aria-labelledby="letters-ask">
        <h2 id="letters-ask">{t("ask")}</h2>
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
        {choices.length === 0 ? (
          <p className="field-hint">{t("noTypes")}</p>
        ) : (
          <>
            <SelectField
              label={t("type")}
              name="letter-type"
              value={type}
              onChange={(e) => setType(e.target.value)}
              hint={chosen ? t(chosen.needsApproval ? "needsApproval" : "issuedAtOnce") : undefined}
              options={choices.map((c) => ({ value: c.key, label: pick(c.names, locale, c.key) }))}
            />
            <RadioGroup
              legend={t("language")}
              name="letter-language"
              value={language}
              onChange={(e) => setLanguage(e.target.value as LetterLanguage)}
              options={(["en", "ar"] as const).map((l) => ({ value: l, label: t(`languages.${l}`) }))}
            />
            <FormField
              label={t("addressee")}
              name="letter-addressee"
              value={addressee}
              maxLength={200}
              hint={t("addresseeHint")}
              onChange={(e) => setAddressee(e.target.value)}
            />
            <div className="ui-control-row">
              <Button variant="primary" busy={busy} busyLabel={t("sending")} disabled={!type} onClick={() => void send()}>
                {t("send")}
              </Button>
            </div>
          </>
        )}
      </section>

      <section className="letters-section" aria-labelledby="letters-mine">
        <h2 id="letters-mine">{t("mine")}</h2>
        {list && list.length > 0 ? (
          <ul className="letters-list">
            {list.map((l) => (
              <li key={l.id}>
                <Link href={`/letters/${l.id}`}>
                  <bdi dir="ltr">{l.reference}</bdi>
                </Link>
                <span>{pick(l.typeNames, locale, l.typeKey)}</span>
                <span className="field-hint">{format.dateTime(new Date(l.createdAt), { dateStyle: "medium" })}</span>
                <Badge tone={letterTone[l.status]}>{t(`statuses.${l.status}`)}</Badge>
                {l.rights.download ? (
                  <a className="letters-download" href={`/api/v1/hr/letters/${l.id}/pdf`} download>
                    {t("download")}
                  </a>
                ) : null}
              </li>
            ))}
          </ul>
        ) : (
          <p className="field-hint">{t("none")}</p>
        )}
      </section>
    </div>
  );
}
