"use client";

import Link from "next/link";
import { useEffect, useState, type ReactNode } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";
import { pick, type InboxItem } from "@/lib/services/types";
import { nameIn, policyHref, type MyPolicyItem } from "@/lib/policies/types";
import type { Me } from "@/lib/api";
import { useNow } from "@/lib/clock";
import { isolate } from "@/lib/bidi";
import { Icon } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import "@/features/services/home.css";

type Meeting = { id: string; subject: string | null; start: string; end: string; isAllDay: boolean; isPrivate: boolean; location: string | null; onlineJoinUrl: string | null; webLink: string; source: string };
type Calendar = { state: string; timeZone: string; items: Meeting[] };
type Weather = { state: string; report: { place: { name: string }; temperatureC: number; condition: string } | null };

type Sources = { me: Me | null; tasks: InboxItem[] | null; policies: MyPolicyItem[] | null; calendar: Calendar | null; weather: Weather | null };

type Action = { label: string; href: string; external?: boolean };
type Row = { key: string; at: number; time: string | null; tag: string | null; title: string; meta: ReactNode; primary: Action; secondary?: Action };

const MAX_ROWS = 3;

async function read<T>(url: string): Promise<T | null> {
  try {
    const response = await fetch(url, { cache: "no-store" });
    return response.ok ? ((await response.json()) as T) : null;
  } catch {
    return null;
  }
}

/**
 * Your briefing (spec 008 FR-056, spec 036 Briefing hero): the dark hero at the top of the home. It greets the viewer
 * with the date and the weather, and lists the next three things that need them, ordered by time: meetings still to
 * come today (spec 018), tasks in the inbox (spec 016) and policies waiting to be accepted (spec 025), each with its
 * main action. Every source is read with the viewer's own session; a source that fails or that the tenant does not have
 * is left out, never guessed. The wording is fixed text, not generated: the AI written briefing of spec 020 is R2.
 */
export default function Briefing({ props }: PlacementProps) {
  const t = useTranslations("home.briefing");
  const locale = useLocale();
  const format = useFormatter();
  const now = useNow();
  const location = typeof props.location === "string" ? props.location.trim() : "";
  const [sources, setSources] = useState<Sources | "loading">("loading");

  useEffect(() => {
    let live = true;
    void Promise.all([
      read<Me>("/api/v1/me"),
      read<InboxItem[]>("/api/v1/tasks"),
      read<MyPolicyItem[]>("/api/v1/me/policies"),
      read<Calendar>("/api/v1/me/calendar?days=1&hidePrivate=false"),
      location ? read<Weather>(`/api/v1/weather?location=${encodeURIComponent(location)}`) : Promise.resolve(null),
    ]).then(([me, tasks, policies, calendar, weather]) => {
      if (live) setSources({ me, tasks, policies, calendar, weather });
    });
    return () => {
      live = false;
    };
  }, [location]);

  const shell = (body: ReactNode, busy = false) => (
    <section className="home-briefing" aria-labelledby="home-briefing-title" aria-busy={busy || undefined} data-testid="home-briefing">
      {body}
    </section>
  );

  if (sources === "loading" || now === null) {
    return shell(
      <>
        <h2 id="home-briefing-title" className="visually-hidden">
          {t("label")}
        </h2>
        <StateView kind="loading" minBlockSize="12rem" />
      </>,
      true,
    );
  }

  const zone = sources.calendar?.timeZone || Intl.DateTimeFormat().resolvedOptions().timeZone;
  const hour = Number(new Intl.DateTimeFormat("en-GB", { timeZone: zone, hour: "numeric", hourCycle: "h23" }).format(new Date(now)));
  const part = hour < 12 ? "morning" : hour < 17 ? "afternoon" : "evening";
  const clock = new Intl.DateTimeFormat(locale, { timeZone: zone, timeStyle: "short" });
  const today = new Intl.DateTimeFormat(locale, { timeZone: zone, weekday: "long", day: "numeric", month: "long" }).format(new Date(now));

  const rows: Row[] = [];
  for (const m of sources.calendar?.state === "ok" || sources.calendar?.state === "degraded" ? sources.calendar.items : []) {
    if (m.isAllDay || m.source !== "outlook" || Date.parse(m.end) < now) continue;
    const join = m.onlineJoinUrl && Date.parse(m.start) - now < 10 * 60_000;
    rows.push({
      key: `m-${m.id}`,
      at: Date.parse(m.start),
      time: clock.format(new Date(m.start)),
      tag: null,
      title: m.isPrivate || !m.subject ? t("privateMeeting") : m.subject,
      meta: m.location ? <bdi>{m.location}</bdi> : t("meeting"),
      primary: join ? { label: t("join"), href: m.onlineJoinUrl!, external: true } : { label: t("open"), href: m.webLink, external: true },
    });
  }
  for (const i of sources.tasks ?? []) {
    const name = i.serviceNames ? pick(i.serviceNames, locale) : i.title ? pick(i.title, locale) : i.name;
    const href = i.requestId ? `/requests/${i.requestId}?task=${i.taskId}` : i.link && /^\/(?!\/)/.test(i.link) ? i.link : "/tasks";
    const due = i.dueAt ? Date.parse(i.dueAt) : null;
    rows.push({
      key: `t-${i.taskId}`,
      at: due ?? Date.parse(i.createdAt) + 7 * 86_400_000,
      time: null,
      tag: due !== null && due < now ? t("overdue") : t("task"),
      title: name,
      meta: (
        <>
          {i.step ? <bdi>{pick(i.step, locale)}</bdi> : null}
          {i.step && i.requester ? " · " : null}
          {i.requester ? t("from", { name: isolate(i.requester.displayName) }) : null}
        </>
      ),
      primary: { label: t("review"), href },
      secondary: { label: t("allTasks"), href: "/tasks" },
    });
  }
  for (const p of sources.policies ?? []) {
    if (!p.due) continue;
    const name = nameIn(p.policy.names, locale);
    rows.push({
      key: `p-${p.policy.id}`,
      at: Date.parse(p.due.dueAt),
      time: null,
      tag: p.due.overdue ? t("overdue") : t("policy"),
      title: t("acceptPolicy", { name: isolate(name) }),
      meta: t("dueOn", { date: format.dateTime(new Date(p.due.dueAt), { dateStyle: "medium" }) }),
      primary: { label: t("readNow"), href: policyHref(p.policy.id) },
      secondary: { label: t("ask"), href: `/assistant?policy=${encodeURIComponent(p.policy.id)}` },
    });
  }
  rows.sort((a, b) => a.at - b.at);
  const shown = rows.slice(0, MAX_ROWS);
  const firstName = sources.me?.displayName.split(" ")[0] ?? "";
  const weather = sources.weather?.state === "ok" && sources.weather.report ? sources.weather.report : null;

  return shell(
    <>
      <p className="home-briefing-date">
        <Icon name="calendar" size={14} />
        <span>
          {today}
          {weather ? <>{" · "}<bdi>{weather.place.name}</bdi>{" · "}{format.number(weather.temperatureC, { style: "unit", unit: "celsius", maximumFractionDigits: 0 })}</> : null}
        </span>
      </p>
      <h2 id="home-briefing-title" className="home-briefing-title">
        {t(`greeting.${part}`, { name: isolate(firstName) })} {shown.length === 0 ? t("clear") : t("waiting", { count: rows.length })}
      </h2>
      {shown.length > 0 ? (
        <ol className="home-briefing-rows">
          {shown.map((r) => (
            <li key={r.key} className="home-briefing-row">
              <span className="home-briefing-time">{r.time ?? r.tag}</span>
              <div className="home-briefing-text">
                <strong>
                  <bdi>{r.title}</bdi>
                </strong>
                <span>{r.meta}</span>
              </div>
              <div className="home-briefing-actions">
                <BriefingLink action={r.primary} primary />
                {r.secondary ? <BriefingLink action={r.secondary} /> : null}
              </div>
            </li>
          ))}
        </ol>
      ) : null}
      <p className="home-briefing-foot">{t("sources")}</p>
    </>,
  );
}

function BriefingLink({ action, primary = false }: { action: Action; primary?: boolean }) {
  const className = primary ? "home-briefing-action" : "home-briefing-action home-briefing-action-quiet";
  return action.external ? (
    <a className={className} href={action.href} target="_blank" rel="noopener noreferrer">
      {action.label}
    </a>
  ) : (
    <Link className={className} href={action.href}>
      {action.label}
    </Link>
  );
}
