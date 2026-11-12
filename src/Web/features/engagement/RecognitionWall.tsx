"use client";

import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { Icon, type IconName } from "@/components/ui/Icon";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { StateView } from "@/components/ui/StateView";
import type { OrgUnitView } from "@/lib/admin/types";
import { unitLabel } from "@/lib/admin/types";
import { languageName } from "@/lib/format";
import { problemMessages } from "@/lib/notifications/types";
import {
  badgeIcons,
  engagementApi,
  localName,
  send,
  type EngagementPerson,
  type LeaderboardView,
  type MyPoints,
  type PointsSettingsView,
  type RecognitionCatalogue,
  type RecognitionOptions,
  type RecognitionPage,
  type RecognitionPreference,
  type RecognitionView,
} from "@/lib/engagement";
import { MentionPicker } from "./PostCard";
import "./engagement.css";

type Loaded = {
  options: RecognitionOptions;
  wall: RecognitionPage;
  preference: RecognitionPreference;
  points: MyPoints | null;
  catalogue: RecognitionCatalogue | null;
  pointsSettings: PointsSettingsView | null;
};

async function call<T>(request: () => Promise<Response>, failed: string): Promise<{ ok: true; value: T } | { ok: false; messages: string[] }> {
  try {
    const response = await request();
    if (response.ok) return { ok: true, value: (response.status === 204 ? null : await response.json()) as T };
    return { ok: false, messages: await problemMessages(response) };
  } catch {
    return { ok: false, messages: [failed] };
  }
}

function useUnits(): OrgUnitView[] {
  const [units, setUnits] = useState<OrgUnitView[]>([]);
  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch("/api/v1/org/units", { cache: "no-store" });
        if (live && response.ok) setUnits(((await response.json()) as OrgUnitView[]).filter((u) => u.status === "active"));
      } catch {
        // Without the unit list, shout-outs go to people only.
      }
    })();
    return () => {
      live = false;
    };
  }, []);
  return units;
}

/**
 * The recognition wall (spec 023 US4): thank people or a team, read every shout-out, and, where points are on, see
 * your points and the leaderboard. Engagement administrators also set values, badges and points here. Which badges
 * are offered, whether points show at all and who may remove a shout-out come from the API.
 */
export function RecognitionWall({ initial, locales }: { initial: Loaded; locales: string[] }) {
  const t = useTranslations("engagement.recognition");
  const [wall, setWall] = useState(initial.wall);
  const [points, setPoints] = useState(initial.points);
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function refreshPoints() {
    if (!initial.options.pointsOn) return;
    const result = await call<MyPoints>(() => fetch(`${engagementApi}/points/me`, { cache: "no-store" }), t("failed"));
    if (result.ok) setPoints(result.value);
  }

  async function more() {
    if (!wall.nextCursor) return;
    setBusy("more");
    const result = await call<RecognitionPage>(() => fetch(`${engagementApi}/recognitions?cursor=${encodeURIComponent(wall.nextCursor ?? "")}`, { cache: "no-store" }), t("failed"));
    if (result.ok) setWall({ items: [...wall.items, ...result.value.items], nextCursor: result.value.nextCursor });
    else setMessages(result.messages);
    setBusy(null);
  }

  async function remove(item: RecognitionView) {
    setBusy(item.id);
    setMessages([]);
    const result = await call<null>(() => send(`${engagementApi}/recognitions/${item.id}`, "DELETE"), t("failed"));
    if (result.ok) setWall((w) => ({ ...w, items: w.items.filter((x) => x.id !== item.id) }));
    else setMessages(result.messages);
    setBusy(null);
  }

  return (
    <div className="engagement">
      <GiveForm
        options={initial.options}
        onGiven={(view) => {
          setWall((w) => ({ ...w, items: [view, ...w.items] }));
          void refreshPoints();
        }}
      />
      {points ? <PointsSummary points={points} /> : null}
      {initial.options.leaderboard ? <Leaderboard /> : null}
      <section className="engagement-section" aria-labelledby="recognition-wall">
        <h2 id="recognition-wall">{t("wallTitle")}</h2>
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {wall.items.length === 0 ? (
          <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} icon="star" />
        ) : (
          <ul className="engagement-feed" aria-label={t("wallLabel")}>
            {wall.items.map((item) => (
              <li key={item.id} id={`recognition-${item.id}`}>
                <RecognitionCard item={item} busy={busy === item.id} onRemove={() => void remove(item)} />
              </li>
            ))}
          </ul>
        )}
        {wall.nextCursor ? (
          <div className="engagement-row">
            <Button variant="secondary" busy={busy === "more"} busyLabel={t("working")} onClick={() => void more()}>
              {t("more")}
            </Button>
          </div>
        ) : null}
      </section>
      <Preferences initial={initial.preference} pointsOn={initial.options.pointsOn} />
      {initial.catalogue ? <CatalogueSettings initial={initial.catalogue} locales={locales} /> : null}
      {initial.pointsSettings?.inPackage ? <PointsSettings initial={initial.pointsSettings} /> : null}
    </div>
  );
}

function names(people: EngagementPerson[]): React.ReactNode {
  return people.map((p, i) => (
    <span key={p.id}>
      {i > 0 ? ", " : null}
      <bdi>{p.displayName}</bdi>
    </span>
  ));
}

export function RecognitionCard({ item, busy = false, onRemove }: { item: RecognitionView; busy?: boolean; onRemove?: () => void }) {
  const t = useTranslations("engagement.recognition");
  const format = useFormatter();
  const locale = useLocale();
  const headingId = `recognition-heading-${item.id}`;
  return (
    <article className="engagement-post" aria-labelledby={headingId}>
      <div className="engagement-post-head">
        <h3 id={headingId} className="engagement-post-author">
          <bdi>{item.giver.displayName}</bdi>
          {" "}
          {t("thanked")}{" "}
          {item.team ? <bdi>{localName(item.team.names, locale)}</bdi> : names(item.recipients)}
        </h3>
        {item.value ? <Badge tone="accent">{localName(item.value.names, locale, item.value.key)}</Badge> : null}
        {item.badge ? (
          <Badge tone="success" icon={item.badge.icon as IconName}>
            {localName(item.badge.names, locale, item.badge.key)}
          </Badge>
        ) : null}
        <p className="engagement-meta">{format.dateTime(new Date(item.createdAt), { dateStyle: "medium", timeStyle: "short" })}</p>
      </div>
      <p dir="auto">{item.message}</p>
      {item.canRemove && onRemove ? (
        <div className="engagement-row">
          <Button size="sm" variant="quiet" icon="trash" busy={busy} busyLabel={t("working")} onClick={onRemove}>
            {t("remove")}
          </Button>
        </div>
      ) : null}
    </article>
  );
}

function GiveForm({ options, onGiven }: { options: RecognitionOptions; onGiven: (view: RecognitionView) => void }) {
  const t = useTranslations("engagement.recognition");
  const locale = useLocale();
  const units = useUnits();
  const [target, setTarget] = useState<"people" | "team">("people");
  const [people, setPeople] = useState<EngagementPerson[]>([]);
  const [team, setTeam] = useState("");
  const [message, setMessage] = useState("");
  const [value, setValue] = useState("");
  const [badge, setBadge] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [given, setGiven] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setGiven(false);
    const result = await call<RecognitionView>(
      () =>
        send(`${engagementApi}/recognitions`, "POST", {
          recipients: target === "people" ? people.map((p) => p.id) : [],
          teamUnitId: target === "team" ? team || null : null,
          message,
          valueId: value || null,
          badgeId: badge || null,
        }),
      t("failed"),
    );
    if (result.ok) {
      onGiven(result.value);
      setPeople([]);
      setTeam("");
      setMessage("");
      setValue("");
      setBadge("");
      setGiven(true);
    } else {
      setMessages(result.messages);
    }
    setBusy(false);
  }

  return (
    <section className="engagement-section" aria-labelledby="recognition-give">
      <h2 id="recognition-give">{t("giveTitle")}</h2>
      <form className="engagement" onSubmit={(e) => void submit(e)}>
        {units.length > 0 ? (
          <SegmentedControl
            label={t("target")}
            name="recognition-target"
            value={target}
            onChange={(v) => setTarget(v as "people" | "team")}
            options={[
              { value: "people", label: t("targetPeople") },
              { value: "team", label: t("targetTeam") },
            ]}
          />
        ) : null}
        {target === "people" ? (
          <MentionPicker value={people} onChange={setPeople} />
        ) : (
          <SelectField
            label={t("team")}
            name="recognition-team"
            value={team}
            onChange={(e) => setTeam(e.target.value)}
            options={[{ value: "", label: t("chooseTeam") }, ...units.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]}
          />
        )}
        <TextAreaField label={t("message")} name="recognition-message" value={message} maxLength={1000} rows={3} dir="auto" onChange={(e) => setMessage(e.target.value)} />
        <div className="engagement-row">
          {options.values.length > 0 ? (
            <SelectField
              label={t("value")}
              name="recognition-value"
              value={value}
              onChange={(e) => setValue(e.target.value)}
              options={[{ value: "", label: t("noValue") }, ...options.values.map((v) => ({ value: v.id, label: localName(v.names, locale, v.key) }))]}
            />
          ) : null}
          {options.badges.length > 0 ? (
            <SelectField
              label={t("badge")}
              name="recognition-badge"
              value={badge}
              onChange={(e) => setBadge(e.target.value)}
              options={[{ value: "", label: t("noBadge") }, ...options.badges.map((b) => ({ value: b.id, label: localName(b.names, locale, b.key) }))]}
            />
          ) : null}
        </div>
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {given ? <Alert tone="success" title={t("given")} live /> : null}
        <div className="engagement-row">
          <Button type="submit" variant="primary" icon="star" busy={busy} busyLabel={t("working")}>
            {t("give")}
          </Button>
        </div>
      </form>
    </section>
  );
}

function PointsSummary({ points }: { points: MyPoints }) {
  const t = useTranslations("engagement.recognition");
  return (
    <section className="engagement-section" aria-labelledby="recognition-points">
      <h2 id="recognition-points">{t("pointsTitle")}</h2>
      <p className="engagement-meta">{t("pointsToday", { points: points.today, cap: points.dailyCap })}</p>
      <p className="engagement-meta">{t("pointsMonth", { points: points.thisMonth })}</p>
      <p className="engagement-meta">{t("pointsTotal", { points: points.total })}</p>
      <p className="engagement-meta">{t("pointsNote")}</p>
    </section>
  );
}

function Leaderboard() {
  const t = useTranslations("engagement.recognition");
  const [period, setPeriod] = useState("month");
  const [board, setBoard] = useState<LeaderboardView | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let live = true;
    void (async () => {
      const result = await call<LeaderboardView>(() => fetch(`${engagementApi}/leaderboard?period=${period}`, { cache: "no-store" }), "");
      if (!live) return;
      if (result.ok) {
        setBoard(result.value);
        setFailed(false);
      } else {
        setFailed(true);
      }
    })();
    return () => {
      live = false;
    };
  }, [period]);

  return (
    <section className="engagement-section" aria-labelledby="recognition-leaderboard">
      <h2 id="recognition-leaderboard">{t("leaderboardTitle")}</h2>
      <SegmentedControl
        label={t("period")}
        name="leaderboard-period"
        value={period}
        onChange={setPeriod}
        options={(["week", "month", "quarter"] as const).map((p) => ({ value: p, label: t(`periods.${p}`) }))}
      />
      {failed ? <StateView kind="error" title={t("leaderboardFailed")} message={t("tryAgain")} /> : null}
      {board && board.entries.length === 0 ? <p className="engagement-meta">{t("leaderboardEmpty")}</p> : null}
      {board && board.entries.length > 0 ? (
        <ol className="engagement-feed" aria-label={t("leaderboardTitle")}>
          {board.entries.map((e) => (
            <li key={e.person.id} className="engagement-row">
              <span className="engagement-meta">{t("rank", { rank: e.rank })}</span>
              <bdi>{e.person.displayName}</bdi>
              <Badge tone="neutral">{t("points", { points: e.points })}</Badge>
            </li>
          ))}
        </ol>
      ) : null}
    </section>
  );
}

function Preferences({ initial, pointsOn }: { initial: RecognitionPreference; pointsOn: boolean }) {
  const t = useTranslations("engagement.recognition");
  const [preference, setPreference] = useState(initial);
  const [messages, setMessages] = useState<string[]>([]);

  async function save(next: Partial<RecognitionPreference>) {
    setMessages([]);
    const result = await call<RecognitionPreference>(() => send(`${engagementApi}/recognition/preferences`, "PUT", next), t("failed"));
    if (result.ok) setPreference(result.value);
    else setMessages(result.messages);
  }

  return (
    <section className="engagement-section" aria-labelledby="recognition-preferences">
      <h2 id="recognition-preferences">{t("preferencesTitle")}</h2>
      <Checkbox label={t("showOnProfile")} checked={preference.showOnProfile} onChange={(e) => void save({ showOnProfile: e.target.checked })} />
      {pointsOn ? <Checkbox label={t("leaderboardOptOut")} checked={preference.leaderboardOptOut} onChange={(e) => void save({ leaderboardOptOut: e.target.checked })} /> : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
    </section>
  );
}

type ValueRow = { key: string; names: Record<string, string>; active: boolean };
type BadgeRow = ValueRow & { icon: string; giverUnitId: string };

function CatalogueSettings({ initial, locales }: { initial: RecognitionCatalogue; locales: string[] }) {
  const t = useTranslations("engagement.recognition");
  const locale = useLocale();
  const units = useUnits();
  const [values, setValues] = useState<ValueRow[]>(initial.values.map((v) => ({ key: v.key, names: v.names, active: v.active })));
  const [badges, setBadges] = useState<BadgeRow[]>(initial.badges.map((b) => ({ key: b.key, names: b.names, active: b.active, icon: b.icon, giverUnitId: b.giverUnitId ?? "" })));
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setSaved(false);
    const result = await call<RecognitionCatalogue>(
      () => send(`${engagementApi}/settings/recognition`, "PUT", { values, badges: badges.map((b) => ({ ...b, giverUnitId: b.giverUnitId || null })) }),
      t("failed"),
    );
    if (result.ok) setSaved(true);
    else setMessages(result.messages);
    setBusy(false);
  }

  const nameFields = (row: ValueRow, prefix: string, change: (names: Record<string, string>) => void) =>
    locales.map((l) => (
      <FormField
        key={l}
        label={t("name", { language: languageName(l, locale) })}
        name={`${prefix}-name-${l}`}
        value={row.names[l] ?? ""}
        maxLength={80}
        dir="auto"
        onChange={(e) => change({ ...row.names, [l]: e.target.value })}
      />
    ));

  return (
    <section className="engagement-section" aria-labelledby="recognition-catalogue">
      <h2 id="recognition-catalogue">{t("catalogueTitle")}</h2>
      <form className="engagement" onSubmit={(e) => void save(e)}>
        <fieldset className="ui-choice-group">
          <legend>{t("values")}</legend>
          {values.map((v, i) => (
            <div key={i} className="engagement-row" role="group" aria-label={t("valueNumber", { number: i + 1 })}>
              <FormField label={t("key")} hint={t("keyHint")} name={`value-key-${i}`} value={v.key} maxLength={40} onChange={(e) => setValues((all) => all.map((x, j) => (j === i ? { ...x, key: e.target.value } : x)))} />
              {nameFields(v, `value-${i}`, (n) => setValues((all) => all.map((x, j) => (j === i ? { ...x, names: n } : x))))}
              <Checkbox label={t("active")} checked={v.active} onChange={(e) => setValues((all) => all.map((x, j) => (j === i ? { ...x, active: e.target.checked } : x)))} />
            </div>
          ))}
          <div className="engagement-row">
            <Button size="sm" icon="plus" onClick={() => setValues((all) => [...all, { key: "", names: {}, active: true }])}>
              {t("addValue")}
            </Button>
          </div>
        </fieldset>
        <fieldset className="ui-choice-group">
          <legend>{t("badges")}</legend>
          {badges.map((b, i) => (
            <div key={i} className="engagement-row" role="group" aria-label={t("badgeNumber", { number: i + 1 })}>
              <Icon name={b.icon as IconName} />
              <FormField label={t("key")} hint={t("keyHint")} name={`badge-key-${i}`} value={b.key} maxLength={40} onChange={(e) => setBadges((all) => all.map((x, j) => (j === i ? { ...x, key: e.target.value } : x)))} />
              {nameFields(b, `badge-${i}`, (n) => setBadges((all) => all.map((x, j) => (j === i ? { ...x, names: n } : x))))}
              <SelectField
                label={t("icon")}
                name={`badge-icon-${i}`}
                value={b.icon}
                onChange={(e) => setBadges((all) => all.map((x, j) => (j === i ? { ...x, icon: e.target.value } : x)))}
                options={badgeIcons.map((icon) => ({ value: icon, label: t(`icons.${icon}`) }))}
              />
              <SelectField
                label={t("givers")}
                name={`badge-unit-${i}`}
                value={b.giverUnitId}
                onChange={(e) => setBadges((all) => all.map((x, j) => (j === i ? { ...x, giverUnitId: e.target.value } : x)))}
                options={[{ value: "", label: t("everyone") }, ...units.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]}
              />
              <Checkbox label={t("active")} checked={b.active} onChange={(e) => setBadges((all) => all.map((x, j) => (j === i ? { ...x, active: e.target.checked } : x)))} />
            </div>
          ))}
          <div className="engagement-row">
            <Button size="sm" icon="plus" onClick={() => setBadges((all) => [...all, { key: "", names: {}, active: true, icon: "star", giverUnitId: "" }])}>
              {t("addBadge")}
            </Button>
          </div>
        </fieldset>
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {saved ? <Alert tone="success" title={t("saved")} live /> : null}
        <div className="engagement-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
            {t("saveCatalogue")}
          </Button>
        </div>
      </form>
    </section>
  );
}

function PointsSettings({ initial }: { initial: PointsSettingsView }) {
  const t = useTranslations("engagement.recognition");
  const [current, setCurrent] = useState(initial);
  const [form, setForm] = useState({
    enabled: initial.enabled,
    leaderboard: initial.leaderboard,
    recognitionReceived: String(initial.recognitionReceived),
    post: String(initial.post),
    pollVote: String(initial.pollVote),
    dailyCap: String(initial.dailyCap),
    giverDailyLimit: String(initial.giverDailyLimit),
  });
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);
  const numbers = ["recognitionReceived", "post", "pollVote", "dailyCap", "giverDailyLimit"] as const;

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setSaved(false);
    const body = { enabled: form.enabled, leaderboard: form.leaderboard, ...Object.fromEntries(numbers.map((k) => [k, Number(form[k])])) };
    const result = await call<PointsSettingsView>(
      () =>
        fetch(`${engagementApi}/settings/points`, {
          method: "PUT",
          headers: { "content-type": "application/json", "if-match": `"p${current.version}"` },
          body: JSON.stringify(body),
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
    <section className="engagement-section" aria-labelledby="recognition-points-settings">
      <h2 id="recognition-points-settings">{t("pointsSettingsTitle")}</h2>
      <form className="engagement" onSubmit={(e) => void save(e)}>
        <Checkbox label={t("pointsEnabled")} hint={t("pointsEnabledHint")} checked={form.enabled} onChange={(e) => setForm((f) => ({ ...f, enabled: e.target.checked }))} />
        <Checkbox label={t("leaderboardEnabled")} checked={form.leaderboard} onChange={(e) => setForm((f) => ({ ...f, leaderboard: e.target.checked }))} />
        <div className="engagement-row">
          {numbers.map((k) => (
            <FormField key={k} label={t(`settings.${k}`)} name={`points-${k}`} type="number" min={0} value={form[k]} onChange={(e) => setForm((f) => ({ ...f, [k]: e.target.value }))} />
          ))}
        </div>
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {saved ? <Alert tone="success" title={t("saved")} live /> : null}
        <div className="engagement-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
            {t("savePoints")}
          </Button>
        </div>
      </form>
    </section>
  );
}
