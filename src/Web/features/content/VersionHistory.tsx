"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import { editHref, type DiffSegment, type VersionCompare, type VersionSummary } from "@/lib/content/types";
import "./content.css";

function Diff({ label, segments }: { label: string; segments: DiffSegment[] }) {
  return (
    <div className="content-diff">
      <h3>{label}</h3>
      <p className="content-diff-text">
        {segments.map((s, i) =>
          s.op === "added" ? (
            <ins key={i}>{s.text}</ins>
          ) : s.op === "removed" ? (
            <del key={i}>{s.text}</del>
          ) : (
            <span key={i}>{s.text}</span>
          ),
        )}
      </p>
    </div>
  );
}

/**
 * Versions of an item with who wrote and approved each (spec 009 US4), a word by word comparison of any two in one
 * language, and restore, which makes a new draft from an older version. Added text is underlined and removed text
 * struck through, so the change reads without color.
 */
export function VersionHistory({ itemId, versions, locales, primary }: { itemId: string; versions: VersionSummary[]; locales: string[]; primary: string }) {
  const t = useTranslations("content.history");
  const viewer = useLocale();
  const router = useRouter();
  const ids = useId();
  const numbers = versions.map((v) => v.number);
  const [from, setFrom] = useState(numbers.length > 1 ? numbers[1]! : numbers[0] ?? 1);
  const [to, setTo] = useState(numbers[0] ?? 1);
  const [locale, setLocale] = useState(primary);
  const [diff, setDiff] = useState<VersionCompare | null>(null);
  const [messages, setMessages] = useState<{ ok: boolean; text: string[] } | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const format = new Intl.DateTimeFormat(viewer, { dateStyle: "medium", timeStyle: "short" });
  const base = `/api/v1/content/items/${encodeURIComponent(itemId)}`;

  async function compare() {
    setBusy("compare");
    setMessages(null);
    const response = await fetch(`${base}/compare?from=${from}&to=${to}&locale=${encodeURIComponent(locale)}`).catch(() => null);
    setBusy(null);
    if (response?.ok) {
      setDiff((await response.json()) as VersionCompare);
    } else {
      setDiff(null);
      setMessages({ ok: false, text: response ? await problemMessages(response) : [t("failed")] });
    }
  }

  async function restore(number: number) {
    setBusy(`restore-${number}`);
    setMessages(null);
    const response = await fetch(`${base}/versions/${number}/restore`, { method: "POST", headers: { "content-type": "application/json" }, body: "{}" }).catch(() => null);
    setBusy(null);
    if (response?.ok) {
      router.push(editHref(itemId));
      return;
    }
    setMessages({ ok: false, text: response ? await problemMessages(response) : [t("failed")] });
  }

  return (
    <div className="content-editor-page">
      {messages ? (
        <div className={messages.ok ? "notice" : "notice notice-error"} role={messages.ok ? "status" : "alert"}>
          {messages.text.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <table className="content-table">
        <caption>{t("versions")}</caption>
        <thead>
          <tr>
            <th scope="col">{t("number")}</th>
            <th scope="col">{t("author")}</th>
            <th scope="col">{t("approver")}</th>
            <th scope="col">{t("note")}</th>
            <th scope="col">{t("when")}</th>
            <th scope="col">
              <span className="visually-hidden">{t("actions")}</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {versions.map((v) => (
            <tr key={v.number}>
              <th scope="row">
                {v.number}
                {v.current ? <span className="ui-badge content-badge" data-tone="accent">{t("current")}</span> : null}
                {v.draft ? <span className="ui-badge content-badge" data-tone="accent">{t("draft")}</span> : null}
                {v.restoredFrom ? <span className="ui-badge content-badge" data-tone="accent">{t("restoredFrom", { number: v.restoredFrom })}</span> : null}
              </th>
              <td>
                <bdi>{v.author.displayName}</bdi>
              </td>
              <td>{v.approver ? <bdi>{v.approver.displayName}</bdi> : t("noApprover")}</td>
              <td>
                {v.changeNote ? <bdi>{v.changeNote}</bdi> : null}
                {v.material ? null : <span className="ui-badge content-badge" data-tone="accent">{t("minor")}</span>}
              </td>
              <td>
                <time dateTime={v.publishedAt ?? v.createdAt}>{format.format(new Date(v.publishedAt ?? v.createdAt))}</time>
              </td>
              <td>
                {!v.current && !v.draft ? (
                  <Button variant="quiet" busy={busy === `restore-${v.number}`} busyLabel={t("restoring")} onClick={() => void restore(v.number)} aria-label={t("restoreVersion", { number: v.number })}>
                    {t("restore")}
                  </Button>
                ) : null}
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {versions.length > 1 ? (
        <section className="content-card" aria-labelledby={`${ids}-compare`}>
          <h2 id={`${ids}-compare`}>{t("compare")}</h2>
          <div className="content-filters">
            <label>
              {t("from")}
              <select value={from} onChange={(e) => setFrom(Number(e.target.value))}>
                {numbers.map((n) => (
                  <option key={n} value={n}>
                    {n}
                  </option>
                ))}
              </select>
            </label>
            <label>
              {t("to")}
              <select value={to} onChange={(e) => setTo(Number(e.target.value))}>
                {numbers.map((n) => (
                  <option key={n} value={n}>
                    {n}
                  </option>
                ))}
              </select>
            </label>
            <label>
              {t("language")}
              <select value={locale} onChange={(e) => setLocale(e.target.value)}>
                {locales.map((l) => (
                  <option key={l} value={l}>
                    {t(`languageValue.${l === "ar" ? "ar" : "en"}`)}
                  </option>
                ))}
              </select>
            </label>
            <Button busy={busy === "compare"} busyLabel={t("comparing")} onClick={() => void compare()}>
              {t("show")}
            </Button>
          </div>
          {diff ? (
            <div aria-live="polite" lang={diff.locale} dir={diff.locale === "ar" ? "rtl" : "ltr"} data-testid="content-diff">
              <p className="content-meta">{t("legend")}</p>
              <Diff label={t("titleField")} segments={diff.title} />
              <Diff label={t("summaryField")} segments={diff.summary} />
              <Diff label={t("bodyField")} segments={diff.body} />
            </div>
          ) : null}
        </section>
      ) : null}
    </div>
  );
}
