"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { BlockEditor } from "@/features/content/editor/BlockEditor";
import { problemMessages } from "@/lib/notifications/types";
import type { BodyDocument } from "@/lib/content/types";
import { languageName } from "@/lib/format";
import {
  communityHref,
  engagementApi,
  localName,
  send,
  type ChannelView,
  type CommunityView,
  type EngagementPerson,
  type FeedPage,
  type PostSaved,
  type PostView,
} from "@/lib/engagement";
import { MentionPicker, NotReachable, PostCard } from "./PostCard";
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
        <Badge tone={community.kind === "open" ? "success" : community.kind === "approval" ? "info" : "neutral"}>{t(`kinds.${community.kind}`)}</Badge>
        {community.orgUnitId ? <Badge tone="accent">{t("directory.department")}</Badge> : null}
        {community.status === "archived" ? <Badge tone="warning">{t("community.archived")}</Badge> : null}
        <span className="engagement-meta">{t("directory.members", { count: community.memberCount })}</span>
        <ButtonLink size="sm" variant="quiet" icon="people" href={`${communityHref(community.id)}/members`}>
          {t("community.membersLink")}
        </ButtonLink>
        {r.canJoin ? (
          <Button size="sm" variant="primary" busy={busy === ":join"} busyLabel={t("working")} onClick={() => void membership(":join")}>
            {community.kind === "approval" ? t("community.askToJoin") : t("community.join")}
          </Button>
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
  const [target, setTarget] = useState(channelId);
  const [version, setVersion] = useState(0);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [notReachable, setNotReachable] = useState<EngagementPerson[]>([]);
  const [posted, setPosted] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setNotReachable([]);
    setPosted(false);
    try {
      const response = await send(`${engagementApi}/communities/${community.id}/posts`, "POST", {
        channelId: target || channelId,
        body,
        mentions: mentions.map((p) => p.id),
      });
      if (response.ok) {
        const saved = (await response.json()) as PostSaved;
        onPosted(saved.post);
        setNotReachable(saved.notReachable);
        setBody(emptyBody());
        setMentions([]);
        setVersion((v) => v + 1);
        setPosted(true);
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
        <MentionPicker value={mentions} onChange={setMentions} />
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {posted ? <Alert tone="success" title={t("composer.posted")} live /> : null}
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
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [added, setAdded] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setAdded(false);
    try {
      const response = await send(`${engagementApi}/communities/${community.id}/channels`, "POST", { key, names });
      if (response.ok) {
        onAdded((await response.json()) as CommunityView);
        setKey("");
        setNames({});
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
