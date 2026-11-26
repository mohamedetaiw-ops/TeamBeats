"use client";

import Link from "next/link";
import { useId, useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { nameIn, reviewsHref, type PolicyListItem, type RegulatorChangeStatus, type RegulatorChangeView } from "@/lib/policies/types";
import { Alert } from "@/components/ui/Alert";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { TextAreaField } from "@/components/ui/FormField";
import "./policies.css";

const tones: Record<RegulatorChangeStatus, BadgeTone> = { new: "info", dismissed: "neutral", actioned: "success" };

/**
 * Regulator changes (spec 025 R3 US10): each item a regulator feed published, as plain text with its https link, and
 * for a new one the two decisions: not relevant, or link to policies and start their reviews. A decided item shows who
 * decided and which policies got a review. The API checks every decision again.
 */
export function RegulatorChangeList({ items, policies }: { items: RegulatorChangeView[]; policies: PolicyListItem[] }) {
  const t = useTranslations("policies.regulators");
  const format = useFormatter();
  const locale = useLocale();
  const router = useRouter();
  const ids = useId();
  const [open, setOpen] = useState<string | null>(null);
  const [chosen, setChosen] = useState<string[]>([]);
  const [reviews, setReviews] = useState(true);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const day = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium" });

  async function send(id: string, action: "dismiss" | "link", body: object) {
    setBusy(id);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/policies/regulator-changes/${encodeURIComponent(id)}:${action}`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
      });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      setOpen(null);
      setChosen([]);
      setNote("");
      router.refresh();
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  function startLink(id: string) {
    setOpen(id);
    setChosen([]);
    setReviews(true);
    setNote("");
    setMessages([]);
  }

  function submit(e: FormEvent, id: string) {
    e.preventDefault();
    if (chosen.length === 0) {
      setMessages([t("choosePolicies")]);
      return;
    }
    void send(id, "link", { policyIds: chosen, startReviews: reviews, note: note.trim() || null });
  }

  return (
    <>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("problem")}>
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </Alert>
      ) : null}
      <ul className="policies-regulator-list" data-testid="regulator-changes">
        {items.map((c) => (
          <li key={c.id} className="policies-card" data-testid="regulator-change" aria-labelledby={`${ids}-${c.id}`}>
            <h2 id={`${ids}-${c.id}`}>
              {c.link ? (
                <a href={c.link} target="_blank" rel="noopener noreferrer">
                  <bdi>{c.title}</bdi>
                </a>
              ) : (
                <bdi>{c.title}</bdi>
              )}
            </h2>
            <p className="policies-meta">
              <Badge tone={tones[c.status]}>{t(`statuses.${c.status}`)}</Badge> <bdi>{c.source}</bdi>
              {" · "}
              {t("published", { date: day(c.publishedAt ?? c.receivedAt) })}
            </p>
            {c.summary ? (
              <p>
                <bdi>{c.summary}</bdi>
              </p>
            ) : null}
            {c.status !== "new" ? (
              <>
                <p className="policies-meta">{t("decided", { name: c.decidedBy?.displayName ?? "", date: c.decidedAt ? day(c.decidedAt) : "" })}</p>
                {c.note ? (
                  <p>
                    <bdi>{c.note}</bdi>
                  </p>
                ) : null}
                {c.policies.length > 0 ? (
                  <ul className="policies-list" aria-label={t("linked")}>
                    {c.policies.map((p) => (
                      <li key={p.policy.id}>
                        <Link href={reviewsHref(p.policy.id)}>
                          <bdi>{t("policyName", { code: p.policy.code, name: nameIn(p.policy.names, locale) })}</bdi>
                        </Link>{" "}
                        {p.reviewStarted ? <Badge tone="success">{t("reviewStarted")}</Badge> : <Badge tone="neutral">{t("reviewKept")}</Badge>}
                      </li>
                    ))}
                  </ul>
                ) : null}
              </>
            ) : open === c.id ? (
              <form onSubmit={(e) => submit(e, c.id)} noValidate aria-label={t("linkTitle")} data-testid="regulator-link-form">
                <fieldset className="ui-choice-group">
                  <legend>{t("policies")}</legend>
                  <div className="ui-choice-list">
                    {policies.map((p) => (
                      <Checkbox
                        key={p.id}
                        label={t("policyName", { code: p.code, name: nameIn(p.names, locale) })}
                        name="policyIds"
                        checked={chosen.includes(p.id)}
                        onChange={(e) => setChosen((now) => (e.target.checked ? [...now, p.id] : now.filter((x) => x !== p.id)))}
                      />
                    ))}
                  </div>
                </fieldset>
                <Checkbox label={t("startReviews")} hint={t("startReviewsHint")} name="startReviews" checked={reviews} onChange={(e) => setReviews(e.target.checked)} />
                <TextAreaField label={t("note")} name="note" value={note} maxLength={2000} onChange={(e) => setNote(e.target.value)} />
                <div className="button-row">
                  <Button type="submit" busy={busy === c.id} busyLabel={t("busy")}>
                    {t("link")}
                  </Button>
                  <Button type="button" variant="secondary" onClick={() => setOpen(null)}>
                    {t("cancel")}
                  </Button>
                </div>
              </form>
            ) : (
              <div className="button-row">
                <Button onClick={() => startLink(c.id)} icon="link">
                  {t("linkAction")}
                </Button>
                <Button variant="secondary" busy={busy === c.id} busyLabel={t("busy")} onClick={() => void send(c.id, "dismiss", {})}>
                  {t("dismiss")}
                </Button>
              </div>
            )}
          </li>
        ))}
      </ul>
    </>
  );
}
