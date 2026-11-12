"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { BlockEditor } from "@/features/content/editor/BlockEditor";
import { problemMessages } from "@/lib/notifications/types";
import type { BodyDocument } from "@/lib/content/types";
import { languageName } from "@/lib/format";
import {
  albumsHref,
  communityHref,
  engagementApi,
  localName,
  send,
  speakUpReplyPath,
  type AnonymousReceipt,
  type AttachmentView,
  type ChannelView,
  type CommunityView,
  type EngagementPerson,
  type FeedPage,
  type PostSaved,
  type PostView,
} from "@/lib/engagement";
import { MentionPicker, NotReachable, PostCard } from "./PostCard";
import { AttachmentPicker } from "./Attachments";
import "./engagement.css";

const emptyBody = (): BodyDocument => ({ blocks: [] });

/** A channel's name; the general channel every community starts with has no stored name and uses the page's label. */
export function useChannelName() {
  const t = useTranslations("engagement");
  const locale = useLocale();
  return (c: ChannelView) => (c.key === "general" && Object.keys(c.names).length === 0 ? t("channels.general") : localName(c.names, locale, c.key));
}

/**
 * A community page (spec 023 US1): join or leave, channels, the composer for members who may post, and the feed with
 * pinned posts first. Everything here reflects the rights the API returned for this viewer.
 */
export function CommunityFeed({ initial, feed, locales }: { initial: CommunityView; feed: FeedPage | null; locales: string[] }) {
  const t = useTranslations("engagement");
  const locale = useLocale();
  const channelName = useChannelName();
  const format = useFormatter();
  const [community, setCommunity] = useState(initial);
  const [channel, setChannel] = useState("");
  const [posts, setPosts] = useState<PostView[]>(feed?.items ?? []);
  const [next, setNext] = useState<string | null>(feed?.nextCursor ?? null);
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const r = community.rights;
  const readable = feed !== null || r.member;

  async function load(channelId: string, cursor: string | null) {
    const params = new URLSearchParams();
    if (channelId) params.set("channelId", channelId);
    if (cursor) params.set("cursor", cursor);
    const response = await fetch(`${engagementApi}/communities/${community.id}/posts?${params}`, { cache: "no-store" });
    if (!response.ok) return;
    const page = (await response.json()) as FeedPage;
    setPosts((all) => (cursor ? [...all, ...page.items] : page.items));
    setNext(page.nextCursor);
  }

  async function membership(action: ":join" | ":leave") {
    setBusy(action);
    setMessages([]);
    setNotice(null);
    try {
      const response = await send(`${engagementApi}/communities/${community.id}${action}`, "POST");
      if (response.ok) {
        const view = (await response.json()) as CommunityView;
        setCommunity(view);
        setNotice(action === ":leave" ? t("community.left") : view.rights.pending ? t("community.requested") : t("community.joinedNotice"));
        if (action === ":join" && view.rights.member) await load(channel, null);
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  const names = Object.fromEntries(community.channels.map((c) => [c.id, channelName(c)]));

  return (
    <div className="engagement">
      <div className="engagement-row">
        {/* A space's discussion has the space's members, listed on the space's own Members page. */}
        {community.spaceId ? null : (
          <Badge tone={community.kind === "open" ? "success" : community.kind === "approval" ? "info" : "neutral"}>{t(`kinds.${community.kind}`)}</Badge>
        )}
        {community.orgUnitId ? <Badge tone="accent">{t("directory.department")}</Badge> : null}
        {community.status === "archived" ? <Badge tone="warning">{t("community.archived")}</Badge> : null}
        <span className="engagement-meta">{t("directory.members", { count: community.memberCount })}</span>
        {community.spaceId ? null : (
          <ButtonLink size="sm" variant="quiet" icon="people" href={`${communityHref(community.id)}/members`}>
            {t("community.membersLink")}
          </ButtonLink>
        )}
        {readable ? (
          <ButtonLink size="sm" variant="quiet" icon="trend-up" href={`${communityHref(community.id)}/polls`}>
            {t("polls.link")}
          </ButtonLink>
        ) : null}
        {readable ? (
          <ButtonLink size="sm" variant="quiet" icon="image" href={albumsHref(community.id)}>
            {t("gallery.link")}
          </ButtonLink>
        ) : null}
        {r.canJoin ? (
          <Button size="sm" variant="primary" busy={busy === ":join"} busyLabel={t("working")} onClick={() => void membership(":join")}>
            {community.kind === "approval" ? t("community.askToJoin") : t("community.join")}
          </Button>
        ) : null}
        {r.canModerate ? (
          <ButtonLink size="sm" variant="quiet" icon="alert" href={`/communities/moderation?communityId=${encodeURIComponent(community.id)}`}>
            {t("moderation.queueLink")}
          </ButtonLink>
        ) : null}
        {r.pending ? <Badge tone="warning">{t("directory.pending")}</Badge> : null}
        {r.canLeave ? (
          <Button size="sm" variant="quiet" busy={busy === ":leave"} busyLabel={t("working")} onClick={() => void membership(":leave")}>
            {t("community.leave")}
          </Button>
        ) : null}
      </div>
      {localName(community.descriptions, locale) ? (
        <p className="engagement-meta" dir="auto">
          {localName(community.descriptions, locale)}
        </p>
      ) : null}
      {r.mutedUntil ? (
        <Alert tone="warning" title={t("moderation.mutedTitle")}>
          {t("moderation.mutedText", { until: format.dateTime(new Date(r.mutedUntil), { dateStyle: "medium", timeStyle: "short" }) })}
        </Alert>
      ) : null}
      {notice ? <Alert tone="success" title={notice} live /> : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {!readable ? (
        <StateView kind="denied" title={t("community.closedTitle")} message={r.pending ? t("community.pendingMessage") : t("community.joinToRead")} icon="lock" />
      ) : (
        <>
          {community.channels.length > 1 ? (
            <SelectField
              label={t("channels.choose")}
              name="channel"
              value={channel}
              onChange={(e) => {
                setChannel(e.target.value);
                void load(e.target.value, null);
              }}
              options={[{ value: "", label: t("channels.all") }, ...community.channels.map((c) => ({ value: c.id, label: channelName(c) }))]}
            />
          ) : null}
          {r.canPost ? (
            <Composer
              community={community}
              channelId={channel || community.channels[0]?.id || ""}
              channelName={channelName}
              onPosted={(p) => setPosts((all) => [...all.filter((x) => x.pinned), p, ...all.filter((x) => !x.pinned)])}
            />
          ) : null}
          {posts.length === 0 ? (
            <StateView kind="empty" title={t("feed.emptyTitle")} message={r.canPost ? t("feed.emptyPoster") : t("feed.empty")} icon="chat" />
          ) : (
            <ul className="engagement-feed" aria-label={t("feed.label")}>
              {posts.map((p) => (
                <li key={p.id}>
                  <PostCard
                    initial={p}
                    canComment={p.canComment}
                    channelName={community.channels.length > 1 ? names[p.channelId] : undefined}
                    onRemoved={(id) => setPosts((all) => all.filter((x) => x.id !== id))}
                  />
                </li>
              ))}
            </ul>
          )}
          {next ? (
            <div className="engagement-row">
              <Button onClick={() => void load(channel, next)}>{t("feed.more")}</Button>
            </div>
          ) : null}
        </>
      )}
      {r.canManage ? <AddChannel community={community} locales={locales} onAdded={setCommunity} /> : null}
    </div>
  );
}

function Composer({
  community,
  channelId,
  channelName,
  onPosted,
}: {
  community: CommunityView;
  channelId: string;
  channelName: (c: ChannelView) => string;
  onPosted: (post: PostView) => void;
}) {
  const t = useTranslations("engagement");
  const locale = useLocale();
  const [body, setBody] = useState<BodyDocument>(emptyBody);
  const [mentions, setMentions] = useState<EngagementPerson[]>([]);
  const [attachments, setAttachments] = useState<AttachmentView[]>([]);
  const [target, setTarget] = useState(channelId);
  const [version, setVersion] = useState(0);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [notReachable, setNotReachable] = useState<EngagementPerson[]>([]);
  const [posted, setPosted] = useState<"published" | "held" | "review" | null>(null);
  const [anonymous, setAnonymous] = useState(false);
  const [replyLink, setReplyLink] = useState<string | null>(null);
  const channel = community.channels.find((c) => c.id === (target || channelId));
  const speakUp = channel?.kind === "speak_up";
  const anonymousAllowed = speakUp && channel?.allowAnonymous === true;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setNotReachable([]);
    setPosted(null);
    setReplyLink(null);
    try {
      if (anonymous && anonymousAllowed) {
        // The person is not named anywhere: the server keeps only the post and a hash of the link token.
        const response = await send(`${engagementApi}/communities/${community.id}/anonymous-posts`, "POST", { channelId: target || channelId, body });
        if (response.ok) {
          const receipt = (await response.json()) as AnonymousReceipt;
          setReplyLink(`${window.location.origin}${speakUpReplyPath}#${receipt.replyToken}`);
          setBody(emptyBody());
          setVersion((v) => v + 1);
        } else {
          setMessages(await problemMessages(response));
        }
        return;
      }
      const response = await send(`${engagementApi}/communities/${community.id}/posts`, "POST", {
        channelId: target || channelId,
        body,
        mentions: mentions.map((p) => p.id),
        attachments: speakUp ? [] : attachments.map((a) => a.id),
      });
      if (response.ok) {
        const saved = (await response.json()) as PostSaved;
        onPosted(saved.post);
        setNotReachable(saved.notReachable);
        setBody(emptyBody());
        setMentions([]);
        setAttachments([]);
        setVersion((v) => v + 1);
        setPosted(saved.post.status !== "held" ? "published" : saved.post.heldReason === "speak_up" ? "review" : "held");
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
    <section className="engagement-section" aria-labelledby="engagement-composer">
      <h2 id="engagement-composer">{t("composer.title")}</h2>
      <form className="engagement" onSubmit={(e) => void submit(e)}>
        {community.channels.length > 1 ? (
          <SelectField
            label={t("composer.channel")}
            name="post-channel"
            value={target || channelId}
            onChange={(e) => setTarget(e.target.value)}
            options={community.channels.map((c) => ({ value: c.id, label: channelName(c) }))}
          />
        ) : null}
        <BlockEditor key={version} label={t("composer.body")} value={body} onChange={setBody} locale={locale} />
        <p className="engagement-meta">{t("composer.hint")}</p>
        {speakUp ? <p className="engagement-meta">{t("speakUp.reviewed")}</p> : null}
        {anonymousAllowed ? (
          <>
            <Checkbox label={t("speakUp.postAnonymously")} hint={t("speakUp.anonymousHint")} checked={anonymous} onChange={(e) => setAnonymous(e.target.checked)} />
            {anonymous ? (
              <Alert tone="info" title={t("speakUp.anonymousTitle")}>
                {t("speakUp.anonymousText")}
              </Alert>
            ) : null}
          </>
        ) : null}
        {anonymous && anonymousAllowed ? null : <MentionPicker value={mentions} onChange={setMentions} />}
        {speakUp ? null : <AttachmentPicker communityId={community.id} value={attachments} onChange={setAttachments} />}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {posted === "published" ? <Alert tone="success" title={t("composer.posted")} live /> : null}
        {posted === "review" ? (
          <Alert tone="info" title={t("speakUp.sentTitle")} live>
            {t("speakUp.sentText")}
          </Alert>
        ) : null}
        {replyLink ? (
          <Alert tone="success" title={t("speakUp.anonymousSentTitle")} live>
            <p>{t("speakUp.anonymousSentText")}</p>
            <FormField label={t("speakUp.replyLink")} name="speak-up-link" value={replyLink} readOnly dir="ltr" onFocus={(e) => e.target.select()} />
          </Alert>
        ) : null}
        {posted === "held" ? (
          <Alert tone="warning" title={t("moderation.heldTitle")} live>
            {t("moderation.heldText")}
          </Alert>
        ) : null}
        <NotReachable people={notReachable} />
        <div className="engagement-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
            {t("composer.submit")}
          </Button>
        </div>
      </form>
    </section>
  );
}

function AddChannel({ community, locales, onAdded }: { community: CommunityView; locales: string[]; onAdded: (view: CommunityView) => void }) {
  const t = useTranslations("engagement");
  const locale = useLocale();
  const [key, setKey] = useState("");
  const [names, setNames] = useState<Record<string, string>>({});
  const [kind, setKind] = useState<"discussion" | "speak_up">("discussion");
  const [allowAnonymous, setAllowAnonymous] = useState(false);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [added, setAdded] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setAdded(false);
    try {
      const response = await send(`${engagementApi}/communities/${community.id}/channels`, "POST", { key, names, kind, allowAnonymous: kind === "speak_up" && allowAnonymous });
      if (response.ok) {
        onAdded((await response.json()) as CommunityView);
        setKey("");
        setNames({});
        setKind("discussion");
        setAllowAnonymous(false);
        setAdded(true);
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
    <section className="engagement-section" aria-labelledby="engagement-channel">
      <h2 id="engagement-channel">{t("channels.addTitle")}</h2>
      <form className="engagement" onSubmit={(e) => void submit(e)}>
        <div className="engagement-row">
          <FormField label={t("channels.key")} name="channel-key" hint={t("channels.keyHint")} value={key} maxLength={40} dir="ltr" onChange={(e) => setKey(e.target.value)} />
          {locales.map((l) => (
            <FormField
              key={l}
              label={t("channels.name", { language: languageName(l, locale) })}
              name={`channel-name-${l}`}
              value={names[l] ?? ""}
              maxLength={120}
              dir="auto"
              onChange={(e) => setNames((all) => ({ ...all, [l]: e.target.value }))}
            />
          ))}
        </div>
        {community.rights.canAddSpeakUp ? (
          <SelectField
            label={t("channels.kind")}
            name="channel-kind"
            value={kind}
            onChange={(e) => setKind(e.target.value as "discussion" | "speak_up")}
            options={[
              { value: "discussion", label: t("channels.kinds.discussion") },
              { value: "speak_up", label: t("channels.kinds.speak_up") },
            ]}
          />
        ) : null}
        {kind === "speak_up" && community.rights.canAllowAnonymous ? (
          <Checkbox label={t("channels.allowAnonymous")} hint={t("channels.allowAnonymousHint")} checked={allowAnonymous} onChange={(e) => setAllowAnonymous(e.target.checked)} />
        ) : null}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {added ? <Alert tone="success" title={t("channels.added")} live /> : null}
        <div className="engagement-row">
          <Button type="submit" icon="plus" busy={busy} busyLabel={t("working")}>
            {t("channels.add")}
          </Button>
        </div>
      </form>
    </section>
  );
}
