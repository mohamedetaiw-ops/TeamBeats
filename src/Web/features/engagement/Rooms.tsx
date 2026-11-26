"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, TextAreaField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { languageName } from "@/lib/format";
import { problemMessages } from "@/lib/notifications/types";
import {
  engagementApi,
  localName,
  roomHref,
  send,
  type CommunityView,
  type EngagementPerson,
  type RoomMessagePage,
  type RoomMessageSaved,
  type RoomMessageView,
  type RoomView,
} from "@/lib/engagement";
import { MentionPicker, NotReachable } from "./PostCard";
import { SummaryControl } from "./Summary";
import { ReportControl, StatusNote } from "./ModerationNotes";
import "./engagement.css";

/**
 * A community's discussion rooms (spec 023 R3 US9). Everyone who reads the community sees its rooms; owners and
 * engagement administrators open new ones. What each person may do comes from the API.
 */
export function RoomList({ community, initial, locales }: { community: CommunityView; initial: RoomView[]; locales: string[] }) {
  const t = useTranslations("engagement.rooms");
  const format = useFormatter();
  const locale = useLocale();
  const [rooms, setRooms] = useState(initial);
  return (
    <div className="engagement">
      {community.rights.canManage && community.status === "active" ? (
        <CreateRoom communityId={community.id} locales={locales} onCreated={(room) => setRooms((all) => [room, ...all])} />
      ) : null}
      {rooms.length === 0 ? (
        <StateView kind="empty" title={t("emptyTitle")} message={community.rights.canManage ? t("emptyManager") : t("empty")} icon="chat" />
      ) : (
        <ul className="engagement-feed" aria-label={t("listLabel")}>
          {rooms.map((room) => (
            <li key={room.id} className="engagement-post">
              <div className="engagement-post-head">
                <h2 className="engagement-post-author">
                  <Link href={roomHref(community.id, room.id)}>
                    <bdi>{localName(room.names, locale)}</bdi>
                  </Link>
                </h2>
                {room.status === "archived" ? <Badge tone="warning">{t("archived")}</Badge> : null}
                <p className="engagement-meta">
                  {room.lastMessageAt ? (
                    <time dateTime={room.lastMessageAt}>{t("lastMessage", { when: format.relativeTime(new Date(room.lastMessageAt)) })}</time>
                  ) : (
                    t("noMessages")
                  )}
                </p>
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function CreateRoom({ communityId, locales, onCreated }: { communityId: string; locales: string[]; onCreated: (room: RoomView) => void }) {
  const t = useTranslations("engagement.rooms");
  const locale = useLocale();
  const [names, setNames] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [created, setCreated] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setCreated(false);
    try {
      const filled = Object.fromEntries(Object.entries(names).filter(([, v]) => v.trim()));
      const response = await send(`${engagementApi}/communities/${communityId}/rooms`, "POST", { names: filled });
      if (response.ok) {
        onCreated((await response.json()) as RoomView);
        setNames({});
        setCreated(true);
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
    <section className="engagement-section" aria-labelledby="engagement-new-room">
      <h2 id="engagement-new-room">{t("createTitle")}</h2>
      <form className="engagement" onSubmit={(e) => void submit(e)}>
        {locales.map((l) => (
          <FormField
            key={l}
            label={t("name", { language: languageName(l, locale) })}
            name={`room-name-${l}`}
            value={names[l] ?? ""}
            maxLength={120}
            dir="auto"
            lang={l}
            onChange={(e) => setNames({ ...names, [l]: e.target.value })}
          />
        ))}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {created ? <Alert tone="success" title={t("created")} live /> : null}
        <div className="engagement-row">
          <Button type="submit" variant="primary" icon="plus" busy={busy} busyLabel={t("working")}>
            {t("create")}
          </Button>
        </div>
      </form>
    </section>
  );
}

/** Adds messages that are not on the list yet and replaces those that are, keeping the order the API gives. */
function merge(list: RoomMessageView[], incoming: RoomMessageView[]): RoomMessageView[] {
  const byId = new Map(list.map((m) => [m.id, m]));
  for (const m of incoming) byId.set(m.id, m);
  return [...byId.values()].sort((a, b) => (a.createdAt === b.createdAt ? a.id.localeCompare(b.id) : a.createdAt.localeCompare(b.createdAt)));
}

/**
 * One room with live chat (FR-027, FR-028). New messages arrive over server-sent events; the stream ends every few
 * minutes and connects again from the last message seen. When the person may no longer read the room the stream says
 * so and the page stops listening.
 */
export function RoomChat({ initialRoom, initial }: { initialRoom: RoomView; initial: RoomMessagePage }) {
  const t = useTranslations("engagement.rooms");
  const [room, setRoom] = useState(initialRoom);
  const [list, setList] = useState(initial.items);
  const [live, setLive] = useState<"connecting" | "live" | "gone">("connecting");
  const [draft, setDraft] = useState("");
  const [mentions, setMentions] = useState<EngagementPerson[]>([]);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [missed, setMissed] = useState<EngagementPerson[]>([]);
  const cursor = useRef(initial.cursor);
  const end = useRef<HTMLLIElement | null>(null);

  useEffect(() => {
    let source: EventSource | null = null;
    let stopped = false;
    function connect() {
      const params = cursor.current ? `?after=${encodeURIComponent(cursor.current)}` : "";
      source = new EventSource(`${engagementApi}/rooms/${room.id}/stream${params}`);
      source.addEventListener("ready", () => setLive("live"));
      source.addEventListener("messages", (e) => {
        setLive("live");
        const page = JSON.parse((e as MessageEvent<string>).data) as RoomMessagePage;
        if (page.cursor) cursor.current = page.cursor;
        setList((all) => merge(all, page.items));
      });
      source.addEventListener("gone", () => {
        stopped = true;
        source?.close();
        setLive("gone");
      });
      source.addEventListener("end", () => {
        source?.close();
        if (!stopped) connect();
      });
    }
    connect();
    return () => {
      stopped = true;
      source?.close();
    };
  }, [room.id]);

  useEffect(() => {
    end.current?.scrollIntoView({ block: "nearest" });
  }, [list.length]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setMissed([]);
    try {
      const response = await send(`${engagementApi}/rooms/${room.id}/messages`, "POST", { body: draft, mentions: mentions.map((m) => m.id) });
      if (response.ok) {
        const saved = (await response.json()) as RoomMessageSaved;
        setList((all) => merge(all, [saved.message]));
        setMissed(saved.notReachable);
        setDraft("");
        setMentions([]);
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  async function remove(id: string) {
    const response = await send(`${engagementApi}/room-messages/${id}`, "DELETE");
    if (response.ok) setList((all) => all.filter((m) => m.id !== id));
    else setMessages(await problemMessages(response));
  }

  async function archive() {
    setBusy(true);
    try {
      const response = await send(`${engagementApi}/rooms/${room.id}:archive`, "POST");
      if (response.ok) setRoom((await response.json()) as RoomView);
      else setMessages(await problemMessages(response));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="engagement">
      <div className="engagement-row">
        {live === "live" ? <Badge tone="success">{t("live")}</Badge> : live === "connecting" ? <Badge tone="neutral">{t("connecting")}</Badge> : null}
        {room.status === "archived" ? <Badge tone="warning">{t("archived")}</Badge> : null}
        {room.rights.canSummarize ? <SummaryControl url={`${engagementApi}/rooms/${room.id}:summarize`} /> : null}
        {room.rights.canArchive ? (
          <Button size="sm" variant="quiet" icon="lock" busy={busy} busyLabel={t("working")} onClick={() => void archive()}>
            {t("archive")}
          </Button>
        ) : null}
      </div>
      {live === "gone" ? <Alert tone="warning" title={t("goneTitle")} live>{t("gone")}</Alert> : null}
      {list.length === 0 ? (
        <StateView kind="empty" title={t("quietTitle")} message={room.rights.canWrite ? t("quietWriter") : t("quiet")} icon="chat" />
      ) : (
        <ol className="engagement-comments" aria-label={t("messagesLabel")} aria-live="polite" aria-relevant="additions">
          {list.map((m) => (
            <RoomMessage key={m.id} message={m} onRemove={() => void remove(m.id)} />
          ))}
          <li ref={end} aria-hidden="true" />
        </ol>
      )}
      {room.rights.canWrite ? (
        <form className="engagement-section" onSubmit={(e) => void submit(e)} aria-label={t("composerLabel")}>
          <TextAreaField
            label={t("message")}
            name="room-message"
            value={draft}
            maxLength={2000}
            rows={2}
            dir="auto"
            onChange={(e) => setDraft(e.target.value)}
          />
          <MentionPicker value={mentions} onChange={setMentions} />
          {messages.length > 0 ? (
            <Alert tone="danger" title={t("failed")} live>
              {messages.join(" ")}
            </Alert>
          ) : null}
          {missed.length > 0 ? <NotReachable people={missed} /> : null}
          <div className="engagement-row">
            <Button type="submit" variant="primary" icon="chat" busy={busy} busyLabel={t("working")} disabled={!draft.trim()}>
              {t("send")}
            </Button>
          </div>
        </form>
      ) : room.status === "active" && live !== "gone" ? (
        <p className="engagement-meta">{t("readOnly")}</p>
      ) : null}
    </div>
  );
}

function RoomMessage({ message, onRemove }: { message: RoomMessageView; onRemove: () => void }) {
  const t = useTranslations("engagement.rooms");
  const tc = useTranslations("engagement");
  const format = useFormatter();
  return (
    <li className="engagement-comment">
      <p className="engagement-meta">
        <bdi>{message.author.active ? message.author.displayName : tc("formerEmployee", { name: message.author.displayName })}</bdi>
        {" · "}
        <time dateTime={message.createdAt}>{format.dateTime(new Date(message.createdAt), { timeStyle: "short" })}</time>
      </p>
      <p className="engagement-comment-body" dir="auto">
        {message.body}
      </p>
      {message.mine ? <StatusNote status={message.status === "deleted" ? "removed" : message.status} heldReason={message.heldReason} removedReason={message.removedReason} /> : null}
      <div className="engagement-actions">
        {message.canReport ? <ReportControl type="room-message" id={message.id} /> : null}
        {message.canRemove ? (
          <Button size="xs" variant="quiet" icon="trash" onClick={onRemove}>
            {t("delete")}
          </Button>
        ) : null}
      </div>
    </li>
  );
}
