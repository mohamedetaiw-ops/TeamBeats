"use client";

import {
  useEffect,
  useId,
  useRef,
  useState,
  type DragEvent,
  type ReactNode,
} from "react";
import { useTranslations } from "next-intl";
import { Avatar } from "./Avatar";
import { Badge, type BadgeTone } from "./Badge";
import { Button } from "./Button";
import { Icon, type IconName } from "./Icon";
import { Menu } from "./Menu";

/*
 * Page patterns (spec 036 T021): the layouts behind the dashboard, assistant chat, profile and board pages, mapped to
 * the spec 032 templates in docs/design-system/templates.md. They arrange regions and take their contents from the
 * feature; they hold no business rule and name no feature component.
 */

export type MosaicTile = {
  key: string;
  /** The share of the row on a wide page. Every tile takes the full row on a narrow one. */
  span?: "third" | "half" | "two-thirds" | "full";
  content: ReactNode;
};

/** Mosaic (spec 032 layout L08): tiles of set widths on a twelve column grid that stack when the region is narrow. */
export function Mosaic({ tiles }: { tiles: readonly MosaicTile[] }) {
  return (
    <div className="ui-mosaic">
      {tiles.map((tile) => (
        <div
          key={tile.key}
          className="ui-mosaic-tile"
          data-span={tile.span ?? "third"}
        >
          {tile.content}
        </div>
      ))}
    </div>
  );
}

export type ConversationGroup = {
  key: string;
  label: string;
  items: readonly { id: string; title: string; time?: string }[];
};

/**
 * Chat layout (spec 032 template T07 with layout L05): past conversations at the start, the conversation in the main
 * region. The current conversation is marked with `aria-current`; on a narrow page the list sits above.
 */
export function ChatLayout({
  label,
  groups,
  current,
  onSelect,
  onNew,
  level = 2,
  children,
}: {
  label: string;
  groups: readonly ConversationGroup[];
  /** The level of the group headings. */
  level?: 2 | 3 | 4;
  current?: string;
  onSelect: (id: string) => void;
  onNew?: () => void;
  children: ReactNode;
}) {
  const t = useTranslations("ui.patterns");
  const id = useId();
  const Heading = `h${level}` as const;
  return (
    <div className="ui-chat">
      <nav className="ui-chat-side" aria-label={label}>
        {onNew ? (
          <Button size="sm" icon="plus" onClick={onNew}>
            {t("newChat")}
          </Button>
        ) : null}
        {groups.map((group) => (
          <div key={group.key} className="ui-chat-group">
            <Heading id={`${id}-${group.key}`}>{group.label}</Heading>
            <ul aria-labelledby={`${id}-${group.key}`}>
              {group.items.map((item) => (
                <li key={item.id}>
                  <button
                    type="button"
                    className="ui-chat-item"
                    aria-current={item.id === current ? "page" : undefined}
                    onClick={() => onSelect(item.id)}
                  >
                    <bdi className="ui-chat-item-title">{item.title}</bdi>
                    {item.time ? (
                      <span className="ui-chat-item-time">{item.time}</span>
                    ) : null}
                  </button>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </nav>
      <div className="ui-chat-main">{children}</div>
    </div>
  );
}

export type ChatMessage = {
  id: string;
  from: "person" | "assistant";
  /** Who said it, for screen readers and the assistant's name line. */
  author: string;
  time?: string;
  body: ReactNode;
  /** Sources, run progress or follow-up actions under an answer. */
  extra?: ReactNode;
};

/**
 * Chat transcript: a log, so new messages are read out as they arrive. The person's messages sit at the end of the
 * line; the assistant's answers take the full width with its mark and name.
 */
export function ChatTranscript({
  label,
  messages,
  pending,
}: {
  label: string;
  messages: readonly ChatMessage[];
  pending?: ReactNode;
}) {
  return (
    <div role="log" aria-label={label}>
      <ol className="ui-chat-log">
        {messages.map((m) =>
          m.from === "person" ? (
            <li key={m.id} className="ui-chat-person">
              <span className="visually-hidden">{m.author}</span>
              <div className="ui-bubble">{m.body}</div>
            </li>
          ) : (
            <li key={m.id} className="ui-chat-answer">
              <span className="ui-chat-mark" aria-hidden="true">
                <Icon name="spark" size={16} />
              </span>
              <div className="ui-chat-answer-body">
                <p className="ui-chat-author">
                  <span>{m.author}</span>
                  {m.time ? (
                    <span className="ui-chat-time">{m.time}</span>
                  ) : null}
                </p>
                <div className="ui-chat-content">{m.body}</div>
                {m.extra}
              </div>
            </li>
          ),
        )}
        {pending ? <li className="ui-chat-answer">{pending}</li> : null}
      </ol>
    </div>
  );
}

export type ProfileFact = { key: string; label: string; value: ReactNode };

/**
 * Profile layout (spec 032 template T09 with layout L03): who or what this is at the top, with its status, key facts
 * and actions; the details in the main column and supporting blocks beside it. Used for agents, people and records.
 */
export function ProfileLayout({
  title,
  level = 1,
  media,
  eyebrow,
  status,
  description,
  facts,
  actions,
  aside,
  children,
}: {
  title: string;
  level?: 1 | 2 | 3;
  /** An avatar, or an icon tile through `ProfileIcon`. */
  media?: ReactNode;
  eyebrow?: ReactNode;
  status?: { text: string; tone?: BadgeTone; icon?: IconName };
  description?: ReactNode;
  facts?: readonly ProfileFact[];
  actions?: ReactNode;
  aside?: ReactNode;
  children: ReactNode;
}) {
  const Heading = `h${level}` as const;
  return (
    <div className="ui-profile">
      <header className="ui-profile-head">
        {media ? <div className="ui-profile-media">{media}</div> : null}
        <div className="ui-profile-text">
          {eyebrow ? <p className="ui-profile-eyebrow">{eyebrow}</p> : null}
          <div className="ui-profile-title">
            <Heading>
              <bdi>{title}</bdi>
            </Heading>
            {status ? (
              <Badge tone={status.tone} icon={status.icon}>
                {status.text}
              </Badge>
            ) : null}
          </div>
          {description ? (
            <p className="ui-profile-description">{description}</p>
          ) : null}
          {facts?.length ? <FactList facts={facts} inline /> : null}
        </div>
        {actions ? <div className="ui-profile-actions">{actions}</div> : null}
      </header>
      <div className="ui-profile-body" data-aside={aside ? true : undefined}>
        <div className="ui-profile-main">{children}</div>
        {aside ? <div className="ui-profile-aside">{aside}</div> : null}
      </div>
    </div>
  );
}

/** The icon tile that stands for a thing without a photo, such as an agent or a request. */
export function ProfileIcon({ icon }: { icon: IconName }) {
  return (
    <span className="ui-icon-tile" data-size="lg">
      <Icon name={icon} size={26} />
    </span>
  );
}

/** Facts as a description list: label then value, in a row under a profile heading or as a block of details. */
export function FactList({
  facts,
  inline = false,
}: {
  facts: readonly ProfileFact[];
  inline?: boolean;
}) {
  return (
    <dl className="ui-facts" data-inline={inline || undefined}>
      {facts.map((f) => (
        <div key={f.key}>
          <dt>{f.label}</dt>
          <dd>{f.value}</dd>
        </div>
      ))}
    </dl>
  );
}

/** A block in the side column of a profile, with its own heading. */
export function ProfileBlock({
  title,
  level = 2,
  children,
}: {
  title: string;
  level?: 2 | 3 | 4;
  children: ReactNode;
}) {
  const id = useId();
  const Heading = `h${level}` as const;
  return (
    <section className="ui-profile-block" aria-labelledby={id}>
      <Heading id={id}>{title}</Heading>
      {children}
    </section>
  );
}

export type TimelineEntry = {
  id: string;
  actor?: string;
  text: ReactNode;
  time: string;
  dateTime: string;
  icon?: IconName;
};

/** Activity timeline: what happened to a record, newest first, each entry with who did it and when. */
export function Timeline({
  label,
  entries,
}: {
  label: string;
  entries: readonly TimelineEntry[];
}) {
  return (
    <ol className="ui-timeline" aria-label={label}>
      {entries.map((e) => (
        <li key={e.id}>
          <span className="ui-timeline-mark">
            {e.actor ? (
              <Avatar name={e.actor} size="sm" />
            ) : (
              <Icon name={e.icon ?? "clock"} size={14} />
            )}
          </span>
          <div className="ui-timeline-text">
            <p>{e.text}</p>
            <time dateTime={e.dateTime}>{e.time}</time>
          </div>
        </li>
      ))}
    </ol>
  );
}

export type BoardColumn = { key: string; label: string };
export type BoardCard = {
  id: string;
  title: string;
  column: string;
  meta?: string;
  assignee?: string;
  due?: { text: string; tone?: BadgeTone };
  tags?: readonly string[];
};

/**
 * Board (spec 032 template T07): cards in columns by stage. A card moves with its menu, which works from the keyboard
 * and a screen reader, or by dragging it to another column; each move is announced and focus stays on the moved card.
 * Moving is the feature's call through `onMove`; the board only reports it.
 */
export function Board({
  label,
  columns,
  cards,
  onMove,
  level = 2,
}: {
  label: string;
  columns: readonly BoardColumn[];
  cards: readonly BoardCard[];
  onMove: (cardId: string, column: string) => void;
  /** The level of the column headings; card titles take the next one. */
  level?: 2 | 3 | 4;
}) {
  const t = useTranslations("ui.patterns");
  const id = useId();
  const root = useRef<HTMLDivElement>(null);
  const [announcement, setAnnouncement] = useState("");
  const [moved, setMoved] = useState<string | null>(null);
  const [over, setOver] = useState<string | null>(null);
  const Heading = `h${level}` as const;
  const CardHeading = `h${(level + 1) as 3 | 4 | 5}` as const;

  useEffect(() => {
    if (!moved) return;
    // After the menu closes and the card renders in its new column, put focus back on it.
    const frame = requestAnimationFrame(() => {
      root.current
        ?.querySelector<HTMLElement>(
          `[data-card="${CSS.escape(moved)}"] .ui-board-move button`,
        )
        ?.focus();
      setMoved(null);
    });
    return () => cancelAnimationFrame(frame);
  }, [moved, cards]);

  function move(card: BoardCard, column: BoardColumn) {
    if (card.column === column.key) return;
    onMove(card.id, column.key);
    setAnnouncement(t("moved", { title: card.title, column: column.label }));
    setMoved(card.id);
  }

  function drop(event: DragEvent<HTMLElement>, column: BoardColumn) {
    event.preventDefault();
    setOver(null);
    const card = cards.find(
      (c) => c.id === event.dataTransfer.getData("text/plain"),
    );
    if (card) move(card, column);
  }

  return (
    <div className="ui-board" ref={root} role="group" aria-label={label}>
      {columns.map((column) => {
        const list = cards.filter((c) => c.column === column.key);
        return (
          <section
            key={column.key}
            className="ui-board-column"
            aria-labelledby={`${id}-${column.key}`}
            data-over={over === column.key || undefined}
            onDragOver={(e) => {
              e.preventDefault();
              setOver(column.key);
            }}
            onDragLeave={() => setOver(null)}
            onDrop={(e) => drop(e, column)}
          >
            <Heading id={`${id}-${column.key}`} className="ui-board-heading">
              <span>{column.label}</span>
              <span className="ui-board-count">
                {t("count", { count: list.length })}
              </span>
            </Heading>
            {list.length ? (
              <ol className="ui-board-cards">
                {list.map((card) => (
                  <li key={card.id}>
                    <article
                      className="ui-board-card"
                      data-card={card.id}
                      draggable
                      onDragStart={(e) =>
                        e.dataTransfer.setData("text/plain", card.id)
                      }
                    >
                      <div className="ui-board-card-head">
                        <CardHeading className="ui-board-card-title">
                          <bdi>{card.title}</bdi>
                        </CardHeading>
                        <span className="ui-board-move">
                          <Menu
                            label={t("move", { title: card.title })}
                            icon="more"
                            iconOnly
                            variant="quiet"
                            size="xs"
                            placement="bottom-end"
                            items={[
                              {
                                kind: "group",
                                key: "to",
                                label: t("moveTo"),
                                items: columns.map((c) => ({
                                  key: c.key,
                                  label: c.label,
                                  radio: true,
                                  checked: c.key === card.column,
                                  onSelect: () => move(card, c),
                                })),
                              },
                            ]}
                          />
                        </span>
                      </div>
                      {card.meta ? (
                        <p className="ui-board-card-meta">{card.meta}</p>
                      ) : null}
                      {card.tags?.length || card.due || card.assignee ? (
                        <div className="ui-board-card-foot">
                          {card.tags?.map((tag) => (
                            <Badge key={tag}>{tag}</Badge>
                          ))}
                          {card.due ? (
                            <Badge tone={card.due.tone} icon="clock">
                              {card.due.text}
                            </Badge>
                          ) : null}
                          {card.assignee ? (
                            <Avatar name={card.assignee} size="sm" />
                          ) : null}
                        </div>
                      ) : null}
                    </article>
                  </li>
                ))}
              </ol>
            ) : (
              <p className="ui-board-empty">{t("empty")}</p>
            )}
          </section>
        );
      })}
      <p className="visually-hidden" role="status">
        {announcement}
      </p>
    </div>
  );
}
