"use client";

import { useEffect, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { reactionKinds, type CommentPage, type CommentView, type ContentPerson, type ReactionKind, type ReactionSummary } from "@/lib/content/types";
import { problemMessages } from "@/lib/notifications/types";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";

const base = (id: string) => `/api/v1/content/items/${encodeURIComponent(id)}`;

async function post(url: string, body?: unknown): Promise<Response> {
  return fetch(url, {
    method: "POST",
    headers: body === undefined ? undefined : { "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

/**
 * Reactions and comments on a content item (spec 009 US9, R1 part). Everything shown comes back from the API for this
 * viewer: held comments only to their author and moderators, moderation actions only to moderators.
 */
export function ContentSocial({ itemId }: { itemId: string }) {
  const t = useTranslations("content.social");
  const locale = useLocale();
  const [reactions, setReactions] = useState<ReactionSummary | null>(null);
  const [page, setPage] = useState<CommentPage | "loading" | "failed">("loading");
  const [draft, setDraft] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  const [mentions, setMentions] = useState<ContentPerson[]>([]);
  const [who, setWho] = useState("");
  const [found, setFound] = useState<ContentPerson[]>([]);

  const [attempt, setAttempt] = useState(0);

  // People to mention come from the directory search, which lists only people the writer may see (spec 009 section 14).
  useEffect(() => {
    const q = who.trim();
    if (q.length < 2) {
      return;
    }
    let live = true;
    const timer = setTimeout(() => {
      fetch(`/api/v1/people?q=${encodeURIComponent(q)}&limit=5`)
        .then(async (r) => (r.ok ? ((await r.json()) as { items: ContentPerson[] }).items : []))
        .then((items) => live && setFound(items.map((p) => ({ id: p.id, displayName: p.displayName }))))
        .catch(() => live && setFound([]));
    }, 250);
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [who]);
  const load = () => setAttempt((a) => a + 1);

  useEffect(() => {
    let live = true;
    Promise.all([fetch(`${base(itemId)}/reactions`), fetch(`${base(itemId)}/comments`)])
      .then(async ([r, c]) => {
        if (!r.ok || !c.ok) throw new Error(String(r.status));
        const summary = (await r.json()) as ReactionSummary;
        const comments = (await c.json()) as CommentPage;
        if (live) {
          setReactions(summary);
          setPage(comments);
        }
      })
      .catch(() => live && setPage("failed"));
    return () => {
      live = false;
    };
  }, [itemId, attempt]);

  async function react(kind: ReactionKind) {
    const response = await post(`${base(itemId)}/reactions`, { kind: reactions?.mine === kind ? "none" : kind });
    if (response.ok) setReactions((await response.json()) as ReactionSummary);
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    try {
      const response = await post(`${base(itemId)}/comments`, { body: draft, mentions: mentions.map((m) => m.id) });
      if (response.ok) {
        const created = (await response.json()) as CommentView;
        setDraft("");
        setMentions([]);
        setWho("");
        setMessages(created.status === "held" ? [t("held")] : []);
        load();
      } else {
        setMessages(await problemMessages(response));
      }
    } finally {
      setBusy(false);
    }
  }

  async function act(comment: CommentView, action: "approve" | "hide" | "report" | "delete") {
    const url = `/api/v1/content/comments/${encodeURIComponent(comment.id)}`;
    const response = action === "delete" ? await fetch(url, { method: "DELETE" }) : await post(`${url}/${action}`);
    if (response.ok) {
      setMessages(action === "report" ? [t("reportedThanks")] : []);
      load();
    } else {
      setMessages(await problemMessages(response));
    }
  }

  const when = (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(iso));

  return (
    <section className="content-social" aria-labelledby="content-social-title">
      <h2 id="content-social-title">{t("title")}</h2>
      {reactions ? (
        <div className="content-reactions" role="group" aria-label={t("reactions")}>
          {reactionKinds.map((k) => (
            <Button key={k} variant={reactions.mine === k ? "primary" : "secondary"} aria-pressed={reactions.mine === k} onClick={() => void react(k)}>
              {t(`kind.${k}`)} <span className="content-count">{reactions.counts[k] ?? 0}</span>
            </Button>
          ))}
        </div>
      ) : null}
      {page === "loading" ? <StateView kind="loading" minBlockSize="6rem" /> : null}
      {page === "failed" ? <StateView kind="error" title={t("failed")} message="" action={<Button onClick={load}>{t("retry")}</Button>} /> : null}
      {typeof page === "object" ? (
        <>
          {page.items.length === 0 ? <p className="content-meta">{t("none")}</p> : null}
          <ol className="content-comments">
            {page.items.map((c) => (
              <li key={c.id} id={`comment-${c.id}`} className="content-comment" data-status={c.status}>
                <p className="content-meta">
                  <bdi>{c.author.displayName}</bdi> · <time dateTime={c.createdAt}>{when(c.createdAt)}</time>
                  {c.status !== "visible" ? <span className="ui-badge content-badge" data-tone="accent">{t(`status.${c.status}`)}</span> : null}
                </p>
                <p className="content-comment-body">
                  <bdi>{c.body}</bdi>
                </p>
                {c.mentions && c.mentions.length > 0 ? (
                  <p className="content-meta">
                    {t("mentioned")}{" "}
                    {c.mentions.map((m, i) => (
                      <span key={m.id}>
                        {i > 0 ? ", " : null}
                        <a href={`/people/${encodeURIComponent(m.id)}`} className="content-mention">
                          <bdi>{m.displayName}</bdi>
                        </a>
                      </span>
                    ))}
                  </p>
                ) : null}
                <div className="content-actions">
                  {page.moderator && c.status === "held" ? <Button onClick={() => void act(c, "approve")}>{t("approve")}</Button> : null}
                  {page.moderator && c.status === "hidden" ? <Button onClick={() => void act(c, "approve")}>{t("restore")}</Button> : null}
                  {page.moderator && c.status === "visible" ? <Button variant="quiet" onClick={() => void act(c, "hide")}>{t("hide")}</Button> : null}
                  {!c.mine && c.status === "visible" && !c.reported ? <Button variant="quiet" onClick={() => void act(c, "report")}>{t("report")}</Button> : null}
                  {c.mine ? <Button variant="quiet" onClick={() => void act(c, "delete")}>{t("delete")}</Button> : null}
                </div>
              </li>
            ))}
          </ol>
          {page.commentsOn ? (
            <form className="content-comment-form" onSubmit={(e) => void submit(e)}>
              <label className="field">
                <span>{t("write")}</span>
                <textarea value={draft} maxLength={2000} rows={3} onChange={(e) => setDraft(e.target.value)} required />
              </label>
              <div className="content-mention-picker">
                <label className="field">
                  <span>{t("mention")}</span>
                  <input type="search" value={who} onChange={(e) => setWho(e.target.value)} autoComplete="off" />
                </label>
                {who.trim().length >= 2 && found.length > 0 ? (
                  <ul className="content-tags" aria-label={t("mentionResults")}>
                    {found
                      .filter((p) => !mentions.some((m) => m.id === p.id))
                      .map((p) => (
                        <li key={p.id}>
                          <Button variant="quiet" onClick={() => setMentions([...mentions, p])} aria-label={t("mentionAdd", { name: p.displayName })}>
                            <bdi>{p.displayName}</bdi>
                          </Button>
                        </li>
                      ))}
                  </ul>
                ) : null}
                {mentions.length > 0 ? (
                  <ul className="content-tags" aria-label={t("mentionChosen")}>
                    {mentions.map((m) => (
                      <li key={m.id}>
                        <Button variant="secondary" onClick={() => setMentions(mentions.filter((x) => x.id !== m.id))} aria-label={t("mentionRemove", { name: m.displayName })}>
                          <bdi>{t("mentionName", { name: m.displayName })}</bdi>
                        </Button>
                      </li>
                    ))}
                  </ul>
                ) : null}
              </div>
              <Button type="submit" variant="primary" busy={busy} busyLabel={t("sending")}>
                {t("send")}
              </Button>
            </form>
          ) : (
            <p className="content-meta">{t("off")}</p>
          )}
        </>
      ) : null}
      {messages.length > 0 ? (
        <div role="status" className="notice">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
    </section>
  );
}
