"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { PersonPicker } from "@/features/services/PersonPicker";
import { call, courseHref, learningApi, localText, send, sessionHref, type RosterView, type SessionView } from "@/lib/learning";
import "./learning.css";

/** Start and end of a session in the viewer's language and time zone. */
function useWhen() {
  const t = useTranslations("learning.sessions");
  const format = useFormatter();
  return (s: SessionView) =>
    t("when", { start: format.dateTime(new Date(s.startsAt), { dateStyle: "medium", timeStyle: "short" }), end: format.dateTime(new Date(s.endsAt), { timeStyle: "short" }) });
}

function Where({ session }: { session: SessionView }) {
  const t = useTranslations("learning.sessions");
  return (
    <>
      {session.place ? <span className="learning-meta">{t("place", { place: session.place })}</span> : null}
      {session.link ? (
        <a href={session.link} target="_blank" rel="noopener noreferrer">
          {t("link")}
        </a>
      ) : null}
    </>
  );
}

const toIso = (local: string) => (local ? new Date(local).toISOString() : null);

/**
 * The sessions of a classroom lesson in the course builder (spec 028 R3 US10): add one with its start, end, place or
 * link, seats and facilitator, and cancel one that has not started. The lesson must be saved first, since sessions
 * belong to its key.
 */
export function SessionsEditor({ courseId, lessonKey }: { courseId: string; lessonKey: string }) {
  const t = useTranslations("learning.sessions");
  const when = useWhen();
  const [sessions, setSessions] = useState<SessionView[] | null>(null);
  const [starts, setStarts] = useState("");
  const [ends, setEnds] = useState("");
  const [place, setPlace] = useState("");
  const [link, setLink] = useState("");
  const [capacity, setCapacity] = useState("12");
  const [facilitator, setFacilitator] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  useEffect(() => {
    let live = true;
    void call<SessionView[]>(() => fetch(`${learningApi}/manage/courses/${courseId}/sessions`), t("failed")).then((r) => {
      if (live) setSessions(r.ok ? r.value.filter((s) => s.lessonKey === lessonKey) : []);
    });
    return () => {
      live = false;
    };
  }, [courseId, lessonKey, t]);

  async function add() {
    setBusy(true);
    setMessages([]);
    const result = await call<SessionView>(
      () =>
        send(`${learningApi}/manage/courses/${courseId}/sessions`, "POST", {
          lessonKey,
          startsAt: toIso(starts),
          endsAt: toIso(ends),
          place: place || null,
          link: link || null,
          capacity: Number(capacity) || null,
          facilitatorId: facilitator,
        }),
      t("failed"),
    );
    if (result.ok) {
      setSessions((list) => [...(list ?? []), result.value].sort((a, b) => a.startsAt.localeCompare(b.startsAt)));
      setStarts("");
      setEnds("");
    } else setMessages(result.messages);
    setBusy(false);
  }

  async function cancel(id: string) {
    setMessages([]);
    const result = await call<SessionView>(() => send(`${learningApi}/manage/sessions/${id}:cancel`, "POST"), t("failed"));
    if (result.ok) setSessions((list) => (list ?? []).map((s) => (s.id === id ? result.value : s)));
    else setMessages(result.messages);
  }

  return (
    <div className="learning-builder-lesson" role="group" aria-labelledby={`sessions-${lessonKey}`}>
      <h4 id={`sessions-${lessonKey}`}>{t("title")}</h4>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {sessions && sessions.length === 0 ? <p className="learning-meta">{t("none")}</p> : null}
      {sessions && sessions.length > 0 ? (
        <ul className="learning-list" aria-label={t("title")}>
          {sessions.map((s) => (
            <li key={s.id}>
              <div className="learning-row">
                <span>{when(s)}</span>
                <Where session={s} />
                <Badge>{t("seats", { booked: s.booked, capacity: s.capacity })}</Badge>
                {s.waiting > 0 ? <Badge tone="warning">{t("waitingCount", { count: s.waiting })}</Badge> : null}
                {s.status === "cancelled" ? <Badge tone="danger">{t("cancelled")}</Badge> : null}
                <Link href={sessionHref(s.id)}>{t("roster")}</Link>
                {s.canManage ? (
                  <ConfirmAction label={t("cancel")} question={t("cancelQuestion")} confirmLabel={t("cancelConfirm")} cancelLabel={t("keep")} onConfirm={() => cancel(s.id)} />
                ) : null}
              </div>
            </li>
          ))}
        </ul>
      ) : null}
      <div className="learning-row">
        <FormField label={t("starts")} name={`${lessonKey}-starts`} type="datetime-local" value={starts} onChange={(e) => setStarts(e.target.value)} />
        <FormField label={t("ends")} name={`${lessonKey}-ends`} type="datetime-local" value={ends} onChange={(e) => setEnds(e.target.value)} />
        <FormField label={t("seatsLabel")} name={`${lessonKey}-capacity`} type="number" min={1} max={500} value={capacity} onChange={(e) => setCapacity(e.target.value)} />
      </div>
      <div className="learning-row">
        <FormField label={t("placeLabel")} name={`${lessonKey}-place`} maxLength={200} dir="auto" value={place} onChange={(e) => setPlace(e.target.value)} />
        <FormField label={t("linkLabel")} name={`${lessonKey}-link`} maxLength={500} dir="ltr" type="url" value={link} onChange={(e) => setLink(e.target.value)} />
        <PersonPicker label={t("facilitator")} value={facilitator} onChange={setFacilitator} />
      </div>
      <div className="learning-row">
        <Button icon="plus" busy={busy} onClick={() => void add()}>
          {t("add")}
        </Button>
      </div>
    </div>
  );
}

/**
 * Seats for a classroom lesson in the player (spec 028 R3 US10): the sessions still to come, with a seat to take or
 * give back, or a place on the waiting list when one is full. The lesson is done once the facilitator records that the
 * learner attended.
 */
export function SessionBooking({ courseId, lessonKey, open, done }: { courseId: string; lessonKey: string; open: boolean; done: boolean }) {
  const t = useTranslations("learning.sessions");
  const when = useWhen();
  const [sessions, setSessions] = useState<SessionView[] | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [busy, setBusy] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    void call<SessionView[]>(() => fetch(`${learningApi}/courses/${courseId}/lessons/${encodeURIComponent(lessonKey)}/sessions`), t("failed")).then((r) => {
      if (live) setSessions(r.ok ? r.value : []);
    });
    return () => {
      live = false;
    };
  }, [courseId, lessonKey, t]);

  async function act(id: string, method: "POST" | "DELETE") {
    setBusy(id);
    setMessages([]);
    const result = await call<SessionView>(() => (method === "POST" ? send(`${learningApi}/sessions/${id}/seat`, "POST") : fetch(`${learningApi}/sessions/${id}/seat`, { method })), t("failed"));
    if (result.ok) {
      // Giving a seat back can change the counts of the others, so the list is read again.
      const fresh = await call<SessionView[]>(() => fetch(`${learningApi}/courses/${courseId}/lessons/${encodeURIComponent(lessonKey)}/sessions`), t("failed"));
      setSessions(fresh.ok ? fresh.value : (list) => (list ?? []).map((s) => (s.id === id ? result.value : s)));
    } else setMessages(result.messages);
    setBusy(null);
  }

  const holding = sessions?.some((s) => s.mySeat === "booked" || s.mySeat === "waiting") ?? false;
  return (
    <div className="learning" aria-live="polite">
      <p className="learning-meta">{done ? t("doneHint") : t("hint")}</p>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {sessions && sessions.length === 0 ? <StateView kind="empty" title={t("noneTitle")} message={t("noneLearner")} icon="calendar" /> : null}
      {sessions && sessions.length > 0 ? (
        <ul className="learning-list" aria-label={t("title")}>
          {sessions.map((s) => {
            const full = s.booked >= s.capacity;
            return (
              <li key={s.id} data-testid="learning-session">
                <div className="learning-row">
                  <span>{when(s)}</span>
                  <Where session={s} />
                  <span className="learning-meta">{t("with", { name: s.facilitator.name })}</span>
                  <Badge tone={full ? "warning" : "info"}>{full ? t("full") : t("free", { count: s.capacity - s.booked })}</Badge>
                  {s.mySeat ? <Badge tone={s.mySeat === "waiting" ? "warning" : "success"}>{s.mySeat === "waiting" ? t("waitingAt", { position: s.myPosition ?? 1 }) : t(`seat.${s.mySeat}`)}</Badge> : null}
                  {s.status === "cancelled" ? <Badge tone="danger">{t("cancelled")}</Badge> : null}
                  {open && !done && !holding && !s.mySeat && s.status === "scheduled" ? (
                    <Button size="sm" variant="primary" icon="calendar" busy={busy === s.id} onClick={() => void act(s.id, "POST")}>
                      {full ? t("wait") : t("book")}
                    </Button>
                  ) : null}
                  {s.mySeat === "booked" || s.mySeat === "waiting" ? (
                    <Button size="sm" variant="quiet" icon="close" busy={busy === s.id} onClick={() => void act(s.id, "DELETE")}>
                      {t("giveBack")}
                    </Button>
                  ) : null}
                </div>
              </li>
            );
          })}
        </ul>
      ) : null}
    </div>
  );
}

/** The sessions the viewer facilitates (spec 028 R3 US10), from two weeks ago on, each opening its roster. */
export function Facilitating({ sessions }: { sessions: SessionView[] }) {
  const t = useTranslations("learning.sessions");
  const locale = useLocale();
  const when = useWhen();
  if (sessions.length === 0) return <StateView kind="empty" title={t("facilitatingEmptyTitle")} message={t("facilitatingEmpty")} icon="calendar" />;
  return (
    <ul className="learning learning-list" aria-label={t("facilitatingTitle")}>
      {sessions.map((s) => (
        <li key={s.id}>
          <div className="learning-row">
            <Link href={sessionHref(s.id)}>
              <bdi>{localText(s.courseTitles, locale)}</bdi>
            </Link>
            <span>{when(s)}</span>
            <Where session={s} />
            <Badge>{t("seats", { booked: s.booked, capacity: s.capacity })}</Badge>
            {s.status === "cancelled" ? <Badge tone="danger">{t("cancelled")}</Badge> : null}
          </div>
        </li>
      ))}
    </ul>
  );
}

/**
 * The roster of a session (spec 028 R3 US10): who has a seat and who is waiting, and, once the session has started,
 * who attended. The API decides whether the viewer may record attendance and checks every mark again.
 */
export function Roster({ initial }: { initial: RosterView }) {
  const t = useTranslations("learning.sessions");
  const locale = useLocale();
  const when = useWhen();
  const [roster, setRoster] = useState(initial);
  const [marks, setMarks] = useState<Record<string, boolean>>(() =>
    Object.fromEntries(initial.seats.filter((s) => s.status !== "waiting").map((s) => [s.learnerId, s.status === "attended"])),
  );
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);
  const s = roster.session;

  async function save() {
    setBusy(true);
    setMessages([]);
    setSaved(false);
    const entries = Object.entries(marks).map(([learnerId, attended]) => ({ learnerId, attended }));
    const result = await call<RosterView>(() => send(`${learningApi}/sessions/${s.id}/attendance`, "PUT", { entries }), t("failed"));
    if (result.ok) {
      setRoster(result.value);
      setSaved(true);
    } else setMessages(result.messages);
    setBusy(false);
  }

  const seated = roster.seats.filter((x) => x.status !== "waiting");
  const waiting = roster.seats.filter((x) => x.status === "waiting");
  return (
    <div className="learning">
      <section className="learning-section" aria-labelledby="session-facts">
        <h2 id="session-facts">{t("details")}</h2>
        <div className="learning-row">
          <Link href={courseHref(s.courseId)}>
            <bdi>{localText(s.courseTitles, locale)}</bdi>
          </Link>
          <span>{when(s)}</span>
          <Where session={s} />
          <span className="learning-meta">{t("with", { name: s.facilitator.name })}</span>
          <Badge>{t("seats", { booked: s.booked, capacity: s.capacity })}</Badge>
          {s.status === "cancelled" ? <Badge tone="danger">{t("cancelled")}</Badge> : null}
        </div>
      </section>
      <section className="learning-section" aria-labelledby="session-attendance">
        <h2 id="session-attendance">{t("attendance")}</h2>
        <p className="learning-meta">{s.canTakeAttendance ? t("attendanceHint") : t("attendanceLater")}</p>
        {saved ? <Alert tone="success" title={t("saved")} live /> : null}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {seated.length === 0 ? (
          <p className="learning-meta">{t("noSeats")}</p>
        ) : (
          <ul className="learning-list" aria-label={t("attendance")}>
            {seated.map((x) => (
              <li key={x.learnerId}>
                <div className="learning-row">
                  <Checkbox
                    name={`attended-${x.learnerId}`}
                    label={<bdi>{x.name}</bdi>}
                    checked={marks[x.learnerId] ?? false}
                    disabled={!s.canTakeAttendance}
                    onChange={(e) => setMarks((m) => ({ ...m, [x.learnerId]: e.target.checked }))}
                  />
                  <Badge tone={x.status === "attended" ? "success" : x.status === "absent" ? "warning" : "info"}>{t(`seat.${x.status}`)}</Badge>
                </div>
              </li>
            ))}
          </ul>
        )}
        {s.canTakeAttendance && seated.length > 0 ? (
          <div className="learning-row">
            <Button variant="primary" icon="check" busy={busy} onClick={() => void save()}>
              {t("saveAttendance")}
            </Button>
          </div>
        ) : null}
      </section>
      {waiting.length > 0 ? (
        <section className="learning-section" aria-labelledby="session-waiting">
          <h2 id="session-waiting">{t("waitingList")}</h2>
          <ol className="learning-list" aria-label={t("waitingList")}>
            {waiting.map((x) => (
              <li key={x.learnerId}>
                <bdi>{x.name}</bdi>
              </li>
            ))}
          </ol>
        </section>
      ) : null}
    </div>
  );
}
