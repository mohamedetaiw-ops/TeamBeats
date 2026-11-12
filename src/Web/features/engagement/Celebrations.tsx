"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { SelectField } from "@/components/ui/FormField";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { StateView } from "@/components/ui/StateView";
import {
  engagementApi,
  send,
  type CelebrationEntry,
  type CelebrationPreference,
  type CelebrationSettingsView,
  type CelebrationsView,
} from "@/lib/engagement";
import { call } from "./RecognitionWall";
import "./engagement.css";

type Loaded = { list: CelebrationsView; preference: CelebrationPreference; settings: CelebrationSettingsView | null };

/** A date with no year, so a birthday never shows one. */
function useDayMonth() {
  const format = useFormatter();
  return (month: number, day: number) => format.dateTime(new Date(Date.UTC(2024, month - 1, day, 12)), { month: "long", day: "numeric", timeZone: "UTC" });
}

/**
 * Celebrations (spec 023 US6): birthdays of people who opted in, work anniversaries and new joiners, for your teams
 * or the whole organization, with a greeting on the day. What shows comes from the API and the tenant's settings.
 */
export function Celebrations({ initial }: { initial: Loaded }) {
  const t = useTranslations("engagement.celebrations");
  const [list, setList] = useState(initial.list);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  async function scope(value: string) {
    setBusy(true);
    const result = await call<CelebrationsView>(() => fetch(`${engagementApi}/celebrations?scope=${value}`, { cache: "no-store" }), t("failed"));
    if (result.ok) setList(result.value);
    setFailed(!result.ok);
    setBusy(false);
  }

  const empty = list.today.length === 0 && list.upcoming.length === 0 && list.joiners.length === 0;
  return (
    <div className="engagement">
      <SegmentedControl
        label={t("scope")}
        name="celebrations-scope"
        value={list.scope}
        onChange={(v) => void scope(v)}
        options={[
          { value: "unit", label: t("scopeUnit") },
          { value: "all", label: t("scopeAll") },
        ]}
      />
      {failed ? <Alert tone="danger" title={t("failed")} live /> : null}
      <div aria-busy={busy || undefined}>
        {empty ? (
          <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} icon="spark" />
        ) : (
          <>
            <Group id="celebrations-today" title={t("todayTitle")} entries={list.today} />
            <Group id="celebrations-upcoming" title={t("upcomingTitle")} entries={list.upcoming} />
            <Group id="celebrations-joiners" title={t("joinersTitle")} entries={list.joiners} />
          </>
        )}
      </div>
      <Preferences initial={initial.preference} />
      {initial.settings ? <Settings initial={initial.settings} /> : null}
    </div>
  );
}

function Group({ id, title, entries }: { id: string; title: string; entries: CelebrationEntry[] }) {
  if (entries.length === 0) return null;
  return (
    <section className="engagement-section" aria-labelledby={id}>
      <h2 id={id}>{title}</h2>
      <ul className="engagement-feed" aria-label={title}>
        {entries.map((e) => (
          <li key={`${e.kind}-${e.person.id}`}>
            <Entry entry={e} />
          </li>
        ))}
      </ul>
    </section>
  );
}

function Entry({ entry }: { entry: CelebrationEntry }) {
  const t = useTranslations("engagement.celebrations");
  const format = useFormatter();
  const dayMonth = useDayMonth();
  const [state, setState] = useState<"idle" | "busy" | "sent" | "failed">("idle");
  const [, month = 1, day = 1] = entry.on.split("-").map(Number);
  const when =
    entry.kind === "joiner"
      ? t("joinedOn", { date: format.dateTime(new Date(`${entry.on}T12:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }) })
      : dayMonth(month, day);

  async function greet() {
    setState("busy");
    const result = await call<null>(() => send(`${engagementApi}/celebrations/${entry.person.id}/greetings`, "POST", { kind: entry.kind }), t("failed"));
    setState(result.ok ? "sent" : "failed");
  }

  return (
    <article className="engagement-post" aria-label={entry.person.displayName}>
      <div className="engagement-post-head">
        <h3 className="engagement-post-author">
          <bdi>{entry.person.displayName}</bdi>
        </h3>
        <Badge tone="accent">{entry.kind === "anniversary" ? t("anniversary", { years: entry.years ?? 1 }) : t(`kinds.${entry.kind}`)}</Badge>
        <p className="engagement-meta">{when}</p>
      </div>
      {entry.canGreet ? (
        <div className="engagement-row">
          {state === "sent" ? (
            <Alert tone="success" title={t("sent")} live />
          ) : (
            <Button size="sm" variant="secondary" icon="spark" busy={state === "busy"} busyLabel={t("working")} onClick={() => void greet()}>
              {t(`greet.${entry.kind}`)}
            </Button>
          )}
          {state === "failed" ? <Alert tone="danger" title={t("failed")} live /> : null}
        </div>
      ) : null}
    </article>
  );
}

function Preferences({ initial }: { initial: CelebrationPreference }) {
  const t = useTranslations("engagement.celebrations");
  const format = useFormatter();
  const monthName = (m: number) => format.dateTime(new Date(Date.UTC(2024, m - 1, 1, 12)), { month: "long", timeZone: "UTC" });
  const [month, setMonth] = useState(initial.birthdayMonth ? String(initial.birthdayMonth) : "");
  const [day, setDay] = useState(initial.birthdayDay ? String(initial.birthdayDay) : "");
  const [optOut, setOptOut] = useState(initial.anniversaryOptOut);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);
  const days = month ? new Date(Date.UTC(2024, Number(month), 0)).getUTCDate() : 31;

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setSaved(false);
    const result = await call<CelebrationPreference>(
      () =>
        send(`${engagementApi}/celebrations/preferences`, "PUT", {
          birthdayMonth: month ? Number(month) : null,
          birthdayDay: month && day ? Number(day) : null,
          anniversaryOptOut: optOut,
        }),
      t("failed"),
    );
    if (result.ok) setSaved(true);
    else setMessages(result.messages);
    setBusy(false);
  }

  return (
    <section className="engagement-section" aria-labelledby="celebrations-preferences">
      <h2 id="celebrations-preferences">{t("preferencesTitle")}</h2>
      <form className="engagement" onSubmit={(e) => void save(e)}>
        {initial.birthdaysOn ? (
          <>
            <p className="engagement-meta">{t("birthdayHint")}</p>
            <div className="engagement-row">
              <SelectField
                label={t("birthdayMonth")}
                name="birthday-month"
                value={month}
                onChange={(e) => setMonth(e.target.value)}
                options={[
                  { value: "", label: t("noBirthday") },
                  ...Array.from({ length: 12 }, (_, i) => ({ value: String(i + 1), label: monthName(i + 1) })),
                ]}
              />
              <SelectField
                label={t("birthdayDay")}
                name="birthday-day"
                value={day}
                disabled={!month}
                onChange={(e) => setDay(e.target.value)}
                options={[{ value: "", label: t("chooseDay") }, ...Array.from({ length: days }, (_, i) => ({ value: String(i + 1), label: String(i + 1) }))]}
              />
            </div>
          </>
        ) : null}
        {initial.anniversariesOn ? <Checkbox label={t("anniversaryOptOut")} checked={optOut} onChange={(e) => setOptOut(e.target.checked)} /> : null}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {saved ? <Alert tone="success" title={t("saved")} live /> : null}
        <div className="engagement-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
            {t("savePreferences")}
          </Button>
        </div>
      </form>
    </section>
  );
}

function Settings({ initial }: { initial: CelebrationSettingsView }) {
  const t = useTranslations("engagement.celebrations");
  const [current, setCurrent] = useState(initial);
  const [form, setForm] = useState({ birthdays: initial.birthdays, anniversaries: initial.anniversaries, joiners: initial.joiners });
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setSaved(false);
    const result = await call<CelebrationSettingsView>(
      () =>
        fetch(`${engagementApi}/settings/celebrations`, {
          method: "PUT",
          headers: { "content-type": "application/json", "if-match": `"c${current.version}"` },
          body: JSON.stringify(form),
        }),
      t("failed"),
    );
    if (result.ok) {
      setCurrent(result.value);
      setSaved(true);
    } else {
      setMessages(result.messages);
    }
    setBusy(false);
  }

  return (
    <section className="engagement-section" aria-labelledby="celebrations-settings">
      <h2 id="celebrations-settings">{t("settingsTitle")}</h2>
      <form className="engagement" onSubmit={(e) => void save(e)}>
        <Checkbox label={t("showBirthdays")} checked={form.birthdays} onChange={(e) => setForm((f) => ({ ...f, birthdays: e.target.checked }))} />
        <Checkbox label={t("showAnniversaries")} hint={t("showAnniversariesHint")} checked={form.anniversaries} onChange={(e) => setForm((f) => ({ ...f, anniversaries: e.target.checked }))} />
        <Checkbox label={t("showJoiners")} checked={form.joiners} onChange={(e) => setForm((f) => ({ ...f, joiners: e.target.checked }))} />
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {saved ? <Alert tone="success" title={t("saved")} live /> : null}
        <div className="engagement-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
            {t("saveSettings")}
          </Button>
        </div>
      </form>
    </section>
  );
}
