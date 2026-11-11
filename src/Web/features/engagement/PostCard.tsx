"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { TextAreaField } from "@/components/ui/FormField";
import { BodyView } from "@/features/content/BodyView";
import { BlockEditor } from "@/features/content/editor/BlockEditor";
import { PersonPicker } from "@/features/services/PersonPicker";
import { problemMessages } from "@/lib/notifications/types";
import type { BodyDocument } from "@/lib/content/types";
import {
  engagementApi,
  hashtagHref,
  postHref,
  reactionKinds,
  send,
  type CommentSaved,
  type CommentView,
  type EngagementPerson,
  type PostSaved,
  type PostView,
  type ReactionCounts,
  type ReactionKind,
} from "@/lib/engagement";

/** Reactions on a post or comment. Choosing your current reaction again takes it back. */
function ReactionBar({ url, initial, label }: { url: string; initial: ReactionCounts; label: string }) {
  const t = useTranslations("engagement");
  const [counts, setCounts] = useState(initial);
  async function react(kind: ReactionKind) {
    const response = await send(url, "PUT", { kind: counts.mine === kind ? null : kind });
    if (response.ok) setCounts((await response.json()) as ReactionCounts);
  }
  return (
    <ul className="engagement-reactions" aria-label={label}>
      {reactionKinds.map((k) => (
        <li key={k}>
          <Button size="xs" variant={counts.mine === k ? "primary" : "quiet"} aria-pressed={counts.mine === k} onClick={() => void react(k)}>
            {t("reactions.count", { reaction: t(`reactions.${k}`), count: counts.counts[k] ?? 0 })}
          </Button>
        </li>
      ))}
    </ul>
  );
}

/** People the author mentioned who cannot read the community; they were not told (spec 023 edge case). */
export function NotReachable({ people }: { people: EngagementPerson[] }) {
  const t = useTranslations("engagement");
  if (people.length === 0) return null;
  return (
    <Alert tone="warning" title={t("notReachable.title")} live>
      <bdi>{people.map((p) => p.displayName).join(", ")}</bdi>
    </Alert>
  );
}

/** Choosing people to mention: each pick comes from the directory search, so only IDs the API returned are sent. */
export function MentionPicker({ value, onChange }: { value: EngagementPerson[]; onChange: (people: EngagementPerson[]) => void }) {
  const t = useTranslations("engagement");
  const [picking, setPicking] = useState<string | null>(null);
  return (
    <div className="engagement">
      {value.length > 0 ? (
        <ul className="engagement-tags" aria-label={t("mentions.chosen")}>
          {value.map((p) => (
            <li key={p.id}>
              <Button size="xs" variant="quiet" icon="close" onClick={() => onChange(value.filter((x) => x.id !== p.id))} aria-label={t("mentions.remove", { name: p.displayName })}>
                <bdi>{p.displayName}</bdi>
              </Button>
            </li>
          ))}
        </ul>
      ) : null}
      <PersonPicker
        label={t("mentions.add")}
        value={picking}
        onChange={async (id) => {
          setPicking(null);
          if (!id || value.some((p) => p.id === id)) return;
          let name = "";
          try {
            const response = await fetch(`/api/v1/people?ids=${encodeURIComponent(id)}`, { cache: "no-store" });
            if (response.ok) name = ((await response.json()) as { items: { displayName: string }[] }).items[0]?.displayName ?? "";
          } catch {
            // The ID is still sent; only the label is missing.
          }
          onChange([...value, { id, displayName: name, active: true }]);
        }}
      />
    </div>
  );
}

function Comments({ post, canComment, onCount }: { post: PostView; canComment: boolean; onCount: (n: number) => void }) {
  const t = useTranslations("engagement");
  const format = useFormatter();
  const [list, setList] = useState<CommentView[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [draft, setDraft] = useState("");
  const [replyTo, setReplyTo] = useState<CommentView | null>(null);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [notReachable, setNotReachable] = useState<EngagementPerson[]>([]);

  const [reload, setReload] = useState(0);
  const load = () => setReload((n) => n + 1);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch(`${engagementApi}/posts/${post.id}/comments`, { cache: "no-store" });
        if (!response.ok) throw new Error(String(response.status));
        const items = (await response.json()) as CommentView[];
        if (!live) return;
        setList(items);
        onCount(items.length);
      } catch {
        if (live) setFailed(true);
      }
    })();
    return () => {
      live = false;
    };
  }, [post.id, onCount, reload]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setNotReachable([]);
    try {
      const response = await send(`${engagementApi}/posts/${post.id}/comments`, "POST", { body: draft, parentId: replyTo?.id ?? null, mentions: [] });
      if (response.ok) {
        setNotReachable(((await response.json()) as CommentSaved).notReachable);
        setDraft("");
        setReplyTo(null);
        load();
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  async function remove(c: CommentView) {
    const response = await send(`${engagementApi}/comments/${c.id}`, "DELETE");
    if (response.ok) load();
    else setMessages(await problemMessages(response));
  }

  if (failed) return <p className="engagement-meta">{t("comments.failed")}</p>;
  if (list === null) return <p className="engagement-meta">{t("comments.loading")}</p>;
  const top = list.filter((c) => !c.parentId);
  const replies = (id: string) => list.filter((c) => c.parentId === id);
  const item = (c: CommentView, reply: boolean) => (
    <li key={c.id} className="engagement-comment">
      <p className="engagement-post-author">
        <bdi>{c.author.active ? c.author.displayName : t("formerEmployee", { name: c.author.displayName })}</bdi>{" "}
        <span className="engagement-meta">
          <time dateTime={c.createdAt}>{format.dateTime(new Date(c.createdAt), { dateStyle: "medium", timeStyle: "short" })}</time>
        </span>
      </p>
      <p className="engagement-comment-body" dir="auto">
        {c.body}
      </p>
      <div className="engagement-actions">
        <ReactionBar url={`${engagementApi}/comments/${c.id}/reaction`} initial={c.reactions} label={t("reactions.commentLabel")} />
        {!reply && canComment ? (
          <Button size="xs" variant="quiet" onClick={() => setReplyTo(c)}>
            {t("comments.reply")}
          </Button>
        ) : null}
        {c.canRemove ? (
          <Button size="xs" variant="quiet" icon="trash" onClick={() => void remove(c)}>
            {t("comments.delete")}
          </Button>
        ) : null}
      </div>
      {!reply && replies(c.id).length > 0 ? <ul className="engagement-comments">{replies(c.id).map((r) => item(r, true))}</ul> : null}
    </li>
  );

  return (
    <div className="engagement">
      {top.length === 0 ? <p className="engagement-meta">{t("comments.none")}</p> : <ul className="engagement-comments" aria-label={t("comments.label")}>{top.map((c) => item(c, false))}</ul>}
      {canComment ? (
        <form className="engagement" onSubmit={(e) => void submit(e)}>
          {replyTo ? (
            <p className="engagement-meta" aria-live="polite">
              {t("comments.replyingTo", { name: replyTo.author.displayName })}{" "}
              <Button size="xs" variant="quiet" onClick={() => setReplyTo(null)}>
                {t("comments.cancelReply")}
              </Button>
            </p>
          ) : null}
          <TextAreaField label={replyTo ? t("comments.replyLabel") : t("comments.add")} name="comment" value={draft} maxLength={2000} rows={2} dir="auto" onChange={(e) => setDraft(e.target.value)} />
          {messages.length > 0 ? (
            <Alert tone="danger" title={t("failed")} live>
              {messages.join(" ")}
            </Alert>
          ) : null}
          <NotReachable people={notReachable} />
          <div className="engagement-row">
            <Button type="submit" size="sm" variant="primary" busy={busy} busyLabel={t("working")} disabled={!draft.trim()}>
              {t("comments.send")}
            </Button>
          </div>
        </form>
      ) : null}
    </div>
  );
}

/**
 * One community post (spec 023 US1): the body in the shared rich text format, hashtags that link to their page,
 * mentions, reactions, comments with one reply level, follow, and pin or edit where the API says the viewer may.
 */
export function PostCard({
  initial,
  canComment,
  channelName,
  showCommunity,
  communityName,
  openComments = false,
  onRemoved,
}: {
  initial: PostView;
  canComment: boolean;
  channelName?: string;
  showCommunity?: boolean;
  communityName?: string;
  openComments?: boolean;
  onRemoved?: (id: string) => void;
}) {
  const t = useTranslations("engagement");
  const format = useFormatter();
  const locale = useLocale();
  const [post, setPost] = useState(initial);
  const [comments, setComments] = useState(openComments);
  const [count, setCount] = useState(initial.commentCount);
  const [editing, setEditing] = useState<{ body: BodyDocument; mentions: EngagementPerson[] } | null>(null);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [notReachable, setNotReachable] = useState<EngagementPerson[]>([]);

  async function act(path: string) {
    setMessages([]);
    const response = await send(`${engagementApi}/posts/${post.id}${path}`, "POST");
    if (response.ok) setPost((await response.json()) as PostView);
    else setMessages(await problemMessages(response));
  }

  async function save() {
    if (!editing) return;
    setBusy(true);
    setMessages([]);
    try {
      const response = await send(`${engagementApi}/posts/${post.id}`, "PUT", { channelId: post.channelId, body: editing.body, mentions: editing.mentions.map((p) => p.id) });
      if (response.ok) {
        const saved = (await response.json()) as PostSaved;
        setPost(saved.post);
        setNotReachable(saved.notReachable);
        setEditing(null);
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    setMessages([]);
    const response = await send(`${engagementApi}/posts/${post.id}`, "DELETE");
    if (response.ok) onRemoved?.(post.id);
    else setMessages(await problemMessages(response));
  }

  const headingId = `post-${post.id}`;
  return (
    <article className="engagement-post" data-pinned={post.pinned || undefined} aria-labelledby={headingId}>
      <div className="engagement-post-head">
        <h3 id={headingId} className="engagement-post-author">
          <bdi>{post.author.active ? post.author.displayName : t("formerEmployee", { name: post.author.displayName })}</bdi>
        </h3>
        <p className="engagement-meta">
          <Link href={postHref(post.communityId, post.id)}>
            <time dateTime={post.publishedAt}>{format.dateTime(new Date(post.publishedAt), { dateStyle: "medium", timeStyle: "short" })}</time>
          </Link>
          {post.editedAt ? ` · ${t("post.edited")}` : null}
          {channelName ? ` · ${channelName}` : null}
          {showCommunity && communityName ? (
            <>
              {" · "}
              <bdi>{communityName}</bdi>
            </>
          ) : null}
        </p>
        {post.pinned ? (
          <Badge tone="accent" icon="star">
            {t("post.pinned")}
          </Badge>
        ) : null}
      </div>
      {editing ? (
        <div className="engagement">
          <BlockEditor label={t("composer.body")} value={editing.body} onChange={(body) => setEditing({ ...editing, body })} locale={locale} />
          <MentionPicker value={editing.mentions} onChange={(mentions) => setEditing({ ...editing, mentions })} />
          <div className="engagement-row">
            <Button variant="primary" size="sm" busy={busy} busyLabel={t("working")} onClick={() => void save()}>
              {t("post.save")}
            </Button>
            <Button variant="quiet" size="sm" onClick={() => setEditing(null)}>
              {t("post.cancel")}
            </Button>
          </div>
        </div>
      ) : (
        <BodyView body={post.body} embedTitle={t("post.embed")} />
      )}
      {post.hashtags.length > 0 ? (
        <ul className="engagement-tags" aria-label={t("post.hashtags")}>
          {post.hashtags.map((tag) => (
            <li key={tag}>
              <Link href={hashtagHref(tag)} dir="auto">
                {t("hashtag.title", { tag })}
              </Link>
            </li>
          ))}
        </ul>
      ) : null}
      {post.mentions.length > 0 ? (
        <p className="engagement-meta">
          {t("post.mentioned")} <bdi>{post.mentions.map((p) => p.displayName).join(", ")}</bdi>
        </p>
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <NotReachable people={notReachable} />
      <ReactionBar url={`${engagementApi}/posts/${post.id}/reaction`} initial={post.reactions} label={t("reactions.postLabel")} />
      <div className="engagement-actions">
        <Button size="sm" variant="quiet" icon="chat" aria-expanded={comments} onClick={() => setComments((c) => !c)}>
          {t("post.comments", { count })}
        </Button>
        <Button size="sm" variant="quiet" icon="bell" aria-pressed={post.following} onClick={() => void act(post.following ? ":unfollow" : ":follow")}>
          {post.following ? t("post.following") : t("post.follow")}
        </Button>
        {post.canPin ? (
          <Button size="sm" variant="quiet" icon="star" onClick={() => void act(post.pinned ? ":unpin" : ":pin")}>
            {post.pinned ? t("post.unpin") : t("post.pin")}
          </Button>
        ) : null}
        {post.canEdit && !editing ? (
          <>
            <Button size="sm" variant="quiet" icon="edit" onClick={() => setEditing({ body: post.body, mentions: post.mentions })}>
              {t("post.edit")}
            </Button>
            <ConfirmAction label={t("post.delete")} question={t("post.deleteQuestion")} confirmLabel={t("post.deleteConfirm")} cancelLabel={t("post.cancel")} onConfirm={remove} />
          </>
        ) : null}
      </div>
      {comments ? <Comments post={post} canComment={canComment} onCount={setCount} /> : null}
    </article>
  );
}
