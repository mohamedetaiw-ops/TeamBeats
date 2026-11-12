"use client";

import Link from "next/link";
import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { SelectField, TextAreaField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { BodyView } from "@/features/content/BodyView";
import { PersonPicker } from "@/features/services/PersonPicker";
import { problemMessages } from "@/lib/notifications/types";
import { engagementApi, localName, postHref, send, type KeywordFilterView, type ModerationItem, type MuteView } from "@/lib/engagement";
import "./engagement.css";

/**
 * The moderators' queue (spec 023 US8): posts and comments held by the keyword filter, hidden after reports, or
 * reported and still shown, across the communities this person moderates. Reporters are never shown. Approving
 * publishes the item and closes its reports; removing needs a reason, which the author reads.
 */
export function ModerationQueue({ initial }: { initial: ModerationItem[] }) {
  const t = useTranslations("engagement.moderation");
  const [items, setItems] = useState(initial);
  const [notice, setNotice] = useState<string | null>(null);

  if (items.length === 0) {
    return (
      <>
        {notice ? <Alert tone="success" title={notice} live /> : null}
        <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} icon="check" />
      </>
    );
  }
  return (
    <div className="engagement">
      {notice ? <Alert tone="success" title={notice} live /> : null}
      <ul className="engagement-feed" aria-label={t("queueLabel")}>
        {items.map((item) => (
          <li key={`${item.type}-${item.id}`}>
            <QueueEntry
              item={item}
              onDone={(message) => {
                setItems((all) => all.filter((x) => x.id !== item.id));
                setNotice(message);
              }}
            />
          </li>
        ))}
      </ul>
    </div>
  );
}

function QueueEntry({ item, onDone }: { item: ModerationItem; onDone: (message: string) => void }) {
  const t = useTranslations("engagement.moderation");
  const format = useFormatter();
  const locale = useLocale();
  const [removing, setRemoving] = useState(false);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const headingId = `queue-${item.id}`;

  async function decide(action: "approve" | "remove") {
    setBusy(true);
    setMessages([]);
    try {
      const response = await send(`${engagementApi}/moderation/${item.type}/${item.id}:${action}`, "POST", action === "remove" ? { reason } : undefined);
      if (response.ok) onDone(action === "approve" ? t("approved") : t("removed"));
      else setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  const state = item.status === "held" ? t("states.held") : item.status === "hidden" ? t("states.hidden") : t("states.reported");
  return (
    <article className="engagement-post" aria-labelledby={headingId}>
      <div className="engagement-post-head">
        <h2 id={headingId} className="engagement-post-author">
          <bdi>{item.author.displayName}</bdi>
        </h2>
        <Badge tone="neutral">{item.type === "post" ? t("types.post") : t("types.comment")}</Badge>
        <Badge tone={item.status === "published" || item.status === "visible" ? "info" : "warning"}>{state}</Badge>
        <p className="engagement-meta">
          <bdi>{localName(item.communityNames, locale)}</bdi>
          {" · "}
          <time dateTime={item.createdAt}>{format.dateTime(new Date(item.createdAt), { dateStyle: "medium", timeStyle: "short" })}</time>
          {" · "}
          <Link href={postHref(item.communityId, item.postId)}>{t("open")}</Link>
        </p>
      </div>
      {item.body ? (
        <BodyView body={item.body} embedTitle={t("embed")} />
      ) : (
        <p className="engagement-comment-body" dir="auto">
          {item.text}
        </p>
      )}
      {item.heldReason === "keyword" ? <p className="engagement-meta">{t("keywordHeld")}</p> : null}
      {item.reports.length > 0 ? (
        <ul className="engagement-tags" aria-label={t("reportsLabel")}>
          {item.reports.map((r) => (
            <li key={r.reason}>
              <Badge tone="danger">{t("reportCount", { reason: t(`reasons.${r.reason}`), count: r.count })}</Badge>
            </li>
          ))}
        </ul>
      ) : null}
      {item.notes.length > 0 ? (
        <ul className="engagement-comments" aria-label={t("notesLabel")}>
          {item.notes.map((n, i) => (
            <li key={i} className="engagement-comment-body" dir="auto">
              {n}
            </li>
          ))}
        </ul>
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {removing ? (
        <div className="engagement">
          <TextAreaField
            label={t("removalReason")}
            name={`removal-${item.id}`}
            hint={t("removalHint")}
            value={reason}
            maxLength={500}
            rows={2}
            dir="auto"
            onChange={(e) => setReason(e.target.value)}
          />
          <div className="engagement-row">
            <Button variant="destructive" size="sm" busy={busy} busyLabel={t("working")} disabled={!reason.trim()} onClick={() => void decide("remove")}>
              {t("confirmRemove")}
            </Button>
            <Button variant="quiet" size="sm" onClick={() => setRemoving(false)}>
              {t("cancel")}
            </Button>
          </div>
        </div>
      ) : (
        <div className="engagement-row">
          <Button variant="primary" size="sm" icon="check" busy={busy} busyLabel={t("working")} onClick={() => void decide("approve")}>
            {item.status === "published" || item.status === "visible" ? t("keep") : t("approve")}
          </Button>
          <Button variant="secondary" size="sm" icon="trash" onClick={() => setRemoving(true)}>
            {t("remove")}
          </Button>
        </div>
      )}
    </article>
  );
}

/** The tenant's keyword filter (FR-019), one word or phrase per line, saved with the version the page read. */
export function KeywordFilterEditor({ initial, eTag }: { initial: KeywordFilterView; eTag: string }) {
  const t = useTranslations("engagement.moderation");
  const [text, setText] = useState(initial.keywords.join("\n"));
  const [tag, setTag] = useState(eTag);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setSaved(false);
    try {
      const response = await fetch(`${engagementApi}/settings/keywords`, {
        method: "PUT",
        headers: { "content-type": "application/json", "if-match": tag },
        body: JSON.stringify({ keywords: text.split("\n") }),
      });
      if (response.ok) {
        const view = (await response.json()) as KeywordFilterView;
        setTag(response.headers.get("etag") ?? tag);
        setText(view.keywords.join("\n"));
        setSaved(true);
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="engagement-section" aria-labelledby="engagement-keywords">
      <h2 id="engagement-keywords">{t("keywordsTitle")}</h2>
      <form className="engagement" onSubmit={(e) => void save(e)}>
        <TextAreaField label={t("keywords")} name="keywords" hint={t("keywordsHint")} value={text} rows={6} dir="auto" onChange={(e) => setText(e.target.value)} />
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {saved ? <Alert tone="success" title={t("keywordsSaved")} live /> : null}
        <div className="engagement-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
            {t("saveKeywords")}
          </Button>
        </div>
      </form>
    </section>
  );
}

const muteDays = [1, 7, 30, 90] as const;

/**
 * Who is muted in a community (spec 023 US8): a muted member still reads but cannot post or comment until the mute
 * ends. Moderators set how long and why; the member is told. The API refuses muting oneself or an owner.
 */
export function CommunityMutes({ communityId, initial }: { communityId: string; initial: MuteView[] }) {
  const t = useTranslations("engagement.moderation");
  const format = useFormatter();
  const [mutes, setMutes] = useState(initial);
  const [person, setPerson] = useState<string | null>(null);
  const [days, setDays] = useState<string>("7");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const base = `${engagementApi}/communities/${communityId}/mutes`;

  async function change(call: () => Promise<Response>, done: string) {
    setBusy(true);
    setMessages([]);
    setNotice(null);
    try {
      const response = await call();
      if (response.ok) {
        setMutes((await response.json()) as MuteView[]);
        setNotice(done);
        return true;
      }
      setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
    return false;
  }

  async function mute() {
    if (!person) return;
    const until = new Date(Date.now() + Number(days) * 86_400_000).toISOString();
    if (await change(() => send(base, "PUT", { userId: person, until, reason }), t("muted"))) {
      setPerson(null);
      setReason("");
    }
  }

  return (
    <section className="engagement-section" aria-labelledby="engagement-mutes">
      <h2 id="engagement-mutes">{t("mutesTitle")}</h2>
      {notice ? <Alert tone="success" title={notice} live /> : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {mutes.length === 0 ? (
        <p className="engagement-meta">{t("noMutes")}</p>
      ) : (
        <ul className="engagement-members" aria-label={t("mutesTitle")}>
          {mutes.map((m) => (
            <li key={m.userId}>
              <span>
                <bdi>{m.displayName}</bdi>
              </span>
              <span className="engagement-meta">
                {t("mutedUntil", { until: format.dateTime(new Date(m.until), { dateStyle: "medium", timeStyle: "short" }) })}
              </span>
              {m.reason ? (
                <span className="engagement-meta" dir="auto">
                  {m.reason}
                </span>
              ) : null}
              <Button size="sm" variant="quiet" disabled={busy} onClick={() => void change(() => send(`${base}/${m.userId}`, "DELETE"), t("unmuted", { name: m.displayName }))}>
                {t("unmute")}
              </Button>
            </li>
          ))}
        </ul>
      )}
      <PersonPicker label={t("mutePerson")} value={person} onChange={setPerson} />
      <div className="engagement-row">
        <SelectField
          label={t("muteFor")}
          name="mute-days"
          value={days}
          onChange={(e) => setDays(e.target.value)}
          options={muteDays.map((d) => ({ value: String(d), label: t("days", { count: d }) }))}
        />
      </div>
      <TextAreaField label={t("muteReason")} name="mute-reason" hint={t("muteReasonHint")} value={reason} maxLength={500} rows={2} dir="auto" onChange={(e) => setReason(e.target.value)} />
      <div className="engagement-row">
        <Button variant="primary" busy={busy} busyLabel={t("working")} disabled={!person || !reason.trim()} onClick={() => void mute()}>
          {t("mute")}
        </Button>
      </div>
    </section>
  );
}
