"use client";

import { useState } from "react";
import Link from "next/link";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { PersonPicker } from "@/features/services/PersonPicker";
import {
  bookingApi,
  bookingDetailHref,
  call,
  otherZone,
  pick,
  send,
  type BookResult,
  type ResourceKind,
  type SearchResult,
  type SiteView,
  type TeamBookResult,
} from "@/lib/booking";
import "./booking.css";

export type FindQuery = { site?: string; kind?: string; zone?: string; date: string };

/**
 * Find a space (spec 031 US2, FR-004 to FR-006, FR-017): choose a site, a kind, a day and a time, and see the resources
 * that are free and that the viewer may book. Booking confirms at once; a repeat books a daily or weekly series and lists
 * the days left out. Every rule is checked by the API; this page only shows what it says.
 */
export function FindSpace({ sites, kinds, initial }: { sites: SiteView[]; kinds: ResourceKind[]; initial: FindQuery }) {
  const t = useTranslations("booking.find");
  const tk = useTranslations("booking.kinds");
  const locale = useLocale();
  const format = useFormatter();
  const [site, setSite] = useState(initial.site && sites.some((s) => s.id === initial.site) ? initial.site : (sites[0]?.id ?? ""));
  const [kind, setKind] = useState<string>(initial.kind && kinds.includes(initial.kind as ResourceKind) ? initial.kind : (kinds[0] ?? ""));
  const [date, setDate] = useState(initial.date);
  const [start, setStart] = useState("09:00");
  const [end, setEnd] = useState("10:00");
  const [capacity, setCapacity] = useState("");
  const [attributes, setAttributes] = useState("");
  const [title, setTitle] = useState("");
  const [repeat, setRepeat] = useState("none");
  const [until, setUntil] = useState("");
  const [result, setResult] = useState<SearchResult | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [booked, setBooked] = useState<BookResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [forUser, setForUser] = useState<string | null>(null);
  const [team, setTeam] = useState<(string | null)[]>([null]);
  const [teamResult, setTeamResult] = useState<TeamBookResult | null>(null);
  const zone = sites.find((s) => s.id === site)?.timeZone ?? "";

  if (sites.length === 0) return <StateView kind="empty" title={t("noSitesTitle")} message={t("noSites")} icon="building" />;

  async function search() {
    setBusy(true);
    setBooked(null);
    const query = new URLSearchParams({ kind, date, start, end });
    if (capacity) query.set("capacity", capacity);
    if (attributes.trim()) query.set("attributes", attributes.trim());
    if (initial.zone && initial.site === site) query.set("zone", initial.zone);
    const found = await call<SearchResult>(() => fetch(`${bookingApi}/sites/${site}/search?${query}`), t("failed"));
    setBusy(false);
    if (!found.ok) {
      setResult(null);
      return setMessages(found.messages);
    }
    setMessages([]);
    setResult(found.value);
  }

  async function book(resourceId: string) {
    setBusy(true);
    const body = {
      resourceId,
      date,
      start,
      end,
      title: title.trim() || null,
      repeat: repeat === "none" ? null : { pattern: repeat, until: until || null },
      forUserId: forUser,
    };
    const made = await call<BookResult>(() => send(`${bookingApi}/bookings`, "POST", body), t("failed"));
    setBusy(false);
    if (!made.ok) return setMessages(made.messages);
    setMessages([]);
    setBooked(made.value);
    setResult((r) => (r ? { ...r, resources: r.resources.filter((x) => x.id !== resourceId) } : r));
  }

  async function bookTeam() {
    setBusy(true);
    setBooked(null);
    const people = team.filter((p): p is string => Boolean(p));
    const body = { siteId: site, zoneId: initial.zone && initial.site === site ? initial.zone : null, kind, date, start, end, title: title.trim() || null, people };
    const made = await call<TeamBookResult>(() => send(`${bookingApi}/bookings:team`, "POST", body), t("failed"));
    setBusy(false);
    if (!made.ok) return setMessages(made.messages);
    setMessages([]);
    setTeamResult(made.value);
  }

  const day = (iso: string) => format.dateTime(new Date(`${iso}T00:00:00`), { dateStyle: "medium" });

  return (
    <div className="booking">
      <form
        className="booking-section booking-form"
        aria-label={t("formLabel")}
        onSubmit={(e) => {
          e.preventDefault();
          void search();
        }}
      >
        <div className="booking-row">
          <SelectField label={t("site")} name="site" value={site} onChange={(e) => setSite(e.target.value)}
            options={sites.map((s) => ({ value: s.id, label: pick(s.names, locale) }))} />
          <SelectField label={t("kind")} name="kind" value={kind} onChange={(e) => setKind(e.target.value)}
            options={kinds.map((k) => ({ value: k, label: tk(k) }))} />
          <FormField label={t("date")} name="date" type="date" value={date} onChange={(e) => setDate(e.target.value)} required />
          <FormField label={t("start")} name="start" type="time" step={900} value={start} onChange={(e) => setStart(e.target.value)} required />
          <FormField label={t("end")} name="end" type="time" step={900} value={end} onChange={(e) => setEnd(e.target.value)} required />
        </div>
        <div className="booking-row">
          <FormField label={t("capacity")} name="capacity" type="number" min={1} max={1000} value={capacity} onChange={(e) => setCapacity(e.target.value)} />
          <FormField label={t("attributes")} name="attributes" hint={t("attributesHint")} value={attributes} onChange={(e) => setAttributes(e.target.value)} />
        </div>
        {zone && otherZone(zone) ? <p className="booking-meta">{t("timeZone", { zone })}</p> : null}
        <div className="booking-row">
          <Button type="submit" variant="primary" icon="search" disabled={busy}>
            {t("search")}
          </Button>
        </div>
      </form>

      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}

      {booked ? (
        <Alert tone="success" title={t("bookedTitle")} live>
          <p>
            {t("booked", { count: booked.bookings.length })}{" "}
            <Link href={bookingDetailHref(booked.bookings[0]!.id)}>{t("open")}</Link>
          </p>
          {booked.skipped.length > 0 ? (
            <ul aria-label={t("skippedLabel")}>
              {booked.skipped.map((s) => (
                <li key={s.date}>{t(`skipped.${s.reason}`, { date: day(s.date) })}</li>
              ))}
            </ul>
          ) : null}
        </Alert>
      ) : null}

      {result ? (
        <section className="booking-section" aria-labelledby="booking-results">
          <h2 id="booking-results">{t("resultsTitle", { count: result.resources.length })}</h2>
          {result.resources.length === 0 ? <p className="booking-meta">{t("none")}</p> : null}
          <div className="booking-row">
            <FormField label={t("title")} name="title" maxLength={200} value={title} onChange={(e) => setTitle(e.target.value)} />
            <SelectField label={t("repeat")} name="repeat" value={repeat} onChange={(e) => setRepeat(e.target.value)}
              options={["none", "daily", "weekly"].map((r) => ({ value: r, label: t(`repeats.${r}`) }))} />
            <PersonPicker label={t("forSomeone")} value={forUser} onChange={setForUser} />
            {repeat !== "none" ? (
              <FormField label={t("until")} name="until" type="date" value={until} min={date} onChange={(e) => setUntil(e.target.value)} required />
            ) : null}
          </div>
          <ul className="booking-list" aria-label={t("resultsLabel")}>
            {result.resources.map((r) => (
              <li key={r.id} className="booking-card">
                <h3>
                  <bdi>{pick(r.names, locale)}</bdi>
                </h3>
                <p className="booking-meta">
                  {[pick(r.floor, locale), pick(r.zone, locale)].filter(Boolean).join(" · ")}
                  {r.capacity ? ` · ${t("seats", { count: r.capacity })}` : ""}
                </p>
                {r.attributes.length > 0 ? (
                  <div className="booking-row">
                    {r.attributes.map((a) => (
                      <Badge key={a}>{a}</Badge>
                    ))}
                  </div>
                ) : null}
                <div className="booking-row">
                  <Button size="sm" icon="check" disabled={busy} onClick={() => void book(r.id)}>
                    {t("book", { name: pick(r.names, locale) })}
                  </Button>
                </div>
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      <section className="booking-section booking-form" aria-labelledby="booking-team">
        <h2 id="booking-team">{t("teamTitle")}</h2>
        <p className="booking-meta">{t("teamIntro")}</p>
        {team.map((p, i) => (
          <PersonPicker key={i} label={t("teamMember", { number: i + 1 })} value={p} onChange={(id) => setTeam((list) => list.map((x, j) => (j === i ? id : x)))} />
        ))}
        <div className="booking-row">
          <Button size="sm" variant="quiet" icon="plus" disabled={team.length >= 50} onClick={() => setTeam((list) => [...list, null])}>
            {t("teamAdd")}
          </Button>
          <Button variant="primary" icon="check" disabled={busy || !team.some(Boolean)} onClick={() => void bookTeam()}>
            {t("teamBook")}
          </Button>
        </div>
        {teamResult ? (
          <ul className="booking-list" aria-label={t("teamResultLabel")}>
            {teamResult.people.map((p) => (
              <li key={p.personId} className="booking-card">
                <strong>
                  <bdi>{p.name ?? p.personId}</bdi>
                </strong>
                {p.booking ? (
                  <Link href={bookingDetailHref(p.booking.id)}>
                    <bdi>{pick(p.booking.resourceNames, locale)}</bdi>
                  </Link>
                ) : (
                  <span className="booking-meta">{t(`teamProblems.${problemKey(p.problem)}`)}</span>
                )}
              </li>
            ))}
          </ul>
        ) : null}
      </section>
    </div>
  );
}

/** The team outcome reasons the page names; any other refusal reads as the rules not allowing it. */
const teamProblems = ["limit", "none_free", "booking_for"] as const;
function problemKey(problem: string | null): string {
  const last = problem?.split(".").pop() ?? "";
  return (teamProblems as readonly string[]).includes(last) ? last : "rules";
}
