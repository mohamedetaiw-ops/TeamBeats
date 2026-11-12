"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { call, learningApi, localText, send, type CatalogueCourse, type TeamMember } from "@/lib/learning";
import { SourceText } from "./parts";
import { reportTones } from "./Reports";
import "./learning.css";

/**
 * My team's learning (spec 028 US5 scenario 3): the viewer's direct reports only, with their courses, and a form to give
 * one of them a published catalogue course. The API checks the reporting line and the course.
 */
export function Team({ initial, courses }: { initial: TeamMember[]; courses: CatalogueCourse[] }) {
  const t = useTranslations("learning.team");
  const locale = useLocale();
  const format = useFormatter();
  const [team, setTeam] = useState(initial);
  const [person, setPerson] = useState(initial[0]?.person.id ?? "");
  const [course, setCourse] = useState(courses[0]?.id ?? "");
  const [dueOn, setDueOn] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);

  async function assign() {
    setBusy(true);
    setMessages([]);
    setNotice(null);
    const result = await call<TeamMember[]>(
      () => send(`${learningApi}/team/${encodeURIComponent(person)}/assignments`, "POST", { courseId: course, dueOn: dueOn || null }),
      t("failed"),
    );
    setBusy(false);
    if (!result.ok) {
      setMessages(result.messages);
      return;
    }
    setTeam(result.value);
    setNotice(t("assigned"));
  }

  if (team.length === 0) return <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} icon="people" />;
  return (
    <div className="learning">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {notice ? (
        <Alert tone="success" title={notice} live>
          {t("assignedHint")}
        </Alert>
      ) : null}
      <section className="learning-section" aria-labelledby="team-people">
        <h2 id="team-people">{t("title", { count: team.length })}</h2>
        <ul className="learning-list" aria-label={t("listLabel")}>
          {team.map((m) => (
            <li key={m.person.id}>
              <strong>
                <bdi>{m.person.displayName}</bdi>
              </strong>
              {m.courses.length === 0 ? <p className="learning-meta">{t("noCourses")}</p> : null}
              {m.courses.map((c) => (
                <div key={c.courseId} className="learning-row">
                  <bdi>{localText(c.titles, locale)}</bdi>
                  <Badge tone={reportTones[c.state]}>{t(`state.${c.state}`)}</Badge>
                  <span className="learning-meta">
                    <SourceText source={c.source} />
                  </span>
                  {c.dueOn && c.state !== "completed" ? (
                    <span className="learning-meta">{t("due", { date: format.dateTime(new Date(`${c.dueOn}T12:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }) })}</span>
                  ) : null}
                </div>
              ))}
            </li>
          ))}
        </ul>
      </section>
      {courses.length > 0 ? (
        <fieldset className="learning-section" disabled={busy}>
          <legend>{t("give")}</legend>
          <p className="learning-meta">{t("giveHint")}</p>
          <div className="learning-row">
            <SelectField
              label={t("person")}
              name="team-person"
              value={person}
              onChange={(e) => setPerson(e.target.value)}
              options={team.map((m) => ({ value: m.person.id, label: m.person.displayName }))}
            />
            <SelectField
              label={t("course")}
              name="team-course"
              value={course}
              onChange={(e) => setCourse(e.target.value)}
              options={courses.map((c) => ({ value: c.id, label: localText(c.titles, locale) }))}
            />
            <FormField label={t("dueOn")} hint={t("dueOnHint")} name="team-due" type="date" value={dueOn} onChange={(e) => setDueOn(e.target.value)} />
          </div>
          <div className="learning-row">
            <Button variant="primary" icon="plus" busy={busy} disabled={!person || !course} onClick={() => void assign()}>
              {t("assign")}
            </Button>
          </div>
        </fieldset>
      ) : null}
    </div>
  );
}
