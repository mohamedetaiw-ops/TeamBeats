"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { StateView } from "@/components/ui/StateView";
import { unitLabel } from "@/lib/admin/types";
import {
  engagementApi,
  localName,
  send,
  type AwardKind,
  type AwardList,
  type AwardNominee,
  type AwardNomineeSummary,
  type AwardPeriodView,
  type EngagementPerson,
} from "@/lib/engagement";
import { MentionPicker } from "./PostCard";
import { call, useUnits } from "./RecognitionWall";
import "./engagement.css";

function nomineeName(nominee: AwardNominee, locale: string): string {
  return nominee.person ? nominee.person.displayName : localName(nominee.unitNames ?? undefined, locale);
}

/**
 * Team and star of the period (spec 023 US5): open periods, nominate with a reason, and, for recognition approvers in
 * scope, read the nominations and choose the winners. Who may open a period or decide comes from the API.
 */
export function Awards({ initial }: { initial: AwardList }) {
  const t = useTranslations("engagement.awards");
  const [periods, setPeriods] = useState(initial.periods);
  const replace = (view: AwardPeriodView) => setPeriods((list) => list.map((p) => (p.id === view.id ? view : p)));

  return (
    <div className="engagement">
      {initial.canCreate ? <OpenForm onOpened={(view) => setPeriods((list) => [view, ...list])} /> : null}
      <section className="engagement-section" aria-labelledby="awards-list">
        <h2 id="awards-list">{t("listTitle")}</h2>
        {periods.length === 0 ? (
          <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} icon="star" />
        ) : (
          <ul className="engagement-feed" aria-label={t("listLabel")}>
            {periods.map((period) => (
              <li key={period.id} id={`award-${period.id}`}>
                <AwardCard period={period} onChanged={replace} />
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

function OpenForm({ onOpened }: { onOpened: (view: AwardPeriodView) => void }) {
  const t = useTranslations("engagement.awards");
  const locale = useLocale();
  const units = useUnits();
  const [kind, setKind] = useState<AwardKind>("person");
  const [title, setTitle] = useState("");
  const [unit, setUnit] = useState("");
  const [closeOn, setCloseOn] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    const result = await call<AwardPeriodView>(
      () => send(`${engagementApi}/awards`, "POST", { kind, title, orgUnitId: unit || null, nominationsCloseOn: closeOn || null }),
      t("failed"),
    );
    if (result.ok) {
      onOpened(result.value);
      setTitle("");
      setUnit("");
      setCloseOn("");
    } else {
      setMessages(result.messages);
    }
    setBusy(false);
  }

  return (
    <section className="engagement-section" aria-labelledby="awards-open">
      <h2 id="awards-open">{t("openTitle")}</h2>
      <form className="engagement" onSubmit={(e) => void submit(e)}>
        <SegmentedControl
          label={t("kind")}
          name="award-kind"
          value={kind}
          onChange={(v) => setKind(v as AwardKind)}
          options={[
            { value: "person", label: t("kindPerson") },
            { value: "team", label: t("kindTeam") },
          ]}
        />
        <FormField label={t("title")} name="award-title" value={title} maxLength={200} dir="auto" onChange={(e) => setTitle(e.target.value)} />
        <div className="engagement-row">
          <SelectField
            label={t("scope")}
            name="award-scope"
            value={unit}
            onChange={(e) => setUnit(e.target.value)}
            options={[{ value: "", label: t("wholeOrganization") }, ...units.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]}
          />
          <FormField label={t("closeOn")} name="award-close-on" type="date" value={closeOn} onChange={(e) => setCloseOn(e.target.value)} />
        </div>
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        <div className="engagement-row">
          <Button type="submit" variant="primary" icon="star" busy={busy} busyLabel={t("working")}>
            {t("open")}
          </Button>
        </div>
      </form>
    </section>
  );
}

function AwardCard({ period, onChanged }: { period: AwardPeriodView; onChanged: (view: AwardPeriodView) => void }) {
  const t = useTranslations("engagement.awards");
  const format = useFormatter();
  const locale = useLocale();
  const headingId = `award-heading-${period.id}`;
  const closeOn = format.dateTime(new Date(`${period.nominationsCloseOn}T12:00:00Z`), { dateStyle: "medium" });
  return (
    <article className="engagement-post" aria-labelledby={headingId}>
      <div className="engagement-post-head">
        <h3 id={headingId} className="engagement-post-author">
          <bdi>{period.title}</bdi>
        </h3>
        <Badge tone="accent">{period.kind === "team" ? t("kindTeam") : t("kindPerson")}</Badge>
        <Badge tone={period.status === "decided" ? "success" : period.nominationsOpen ? "info" : "neutral"}>
          {period.status === "decided" ? t("statusDecided") : period.nominationsOpen ? t("statusOpen") : t("statusClosed")}
        </Badge>
        <p className="engagement-meta">{period.unitNames ? t("scopeUnit", { unit: localName(period.unitNames, locale) }) : t("scopeAll")}</p>
        <p className="engagement-meta">{t("closesOn", { date: closeOn })}</p>
      </div>
      {period.winners.length > 0 ? (
        <div>
          <h4>{t("winners")}</h4>
          <ul aria-label={t("winners")}>
            {period.winners.map((w) => (
              <li key={w.id}>
                <bdi>{nomineeName(w, locale)}</bdi>
              </li>
            ))}
          </ul>
        </div>
      ) : null}
      {period.myNomination ? (
        <p className="engagement-meta">
          {t("myNomination")} <bdi>{nomineeName(period.myNomination.nominee, locale)}</bdi>
        </p>
      ) : null}
      {period.nominationsOpen ? <NominateForm period={period} onSaved={onChanged} /> : null}
      {period.canDecide ? <Decide period={period} onDecided={onChanged} /> : null}
      {period.announcementItemId ? (
        <div className="engagement-row">
          <ButtonLink variant="secondary" icon="megaphone" href={`/content/items/${encodeURIComponent(period.announcementItemId)}`}>
            {t("announcement")}
          </ButtonLink>
        </div>
      ) : null}
    </article>
  );
}

function NominateForm({ period, onSaved }: { period: AwardPeriodView; onSaved: (view: AwardPeriodView) => void }) {
  const t = useTranslations("engagement.awards");
  const locale = useLocale();
  const units = useUnits();
  const [people, setPeople] = useState<EngagementPerson[]>([]);
  const [team, setTeam] = useState("");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setSaved(false);
    const nomineeId = period.kind === "team" ? team || null : (people[0]?.id ?? null);
    const result = await call<AwardPeriodView>(() => send(`${engagementApi}/awards/${period.id}/nomination`, "PUT", { nomineeId, reason }), t("failed"));
    if (result.ok) {
      onSaved(result.value);
      setPeople([]);
      setTeam("");
      setReason("");
      setSaved(true);
    } else {
      setMessages(result.messages);
    }
    setBusy(false);
  }

  return (
    <form className="engagement" aria-label={t("nominateLabel", { award: period.title })} onSubmit={(e) => void submit(e)}>
      {period.kind === "team" ? (
        <SelectField
          label={t("team")}
          name={`award-team-${period.id}`}
          value={team}
          onChange={(e) => setTeam(e.target.value)}
          options={[{ value: "", label: t("chooseTeam") }, ...units.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]}
        />
      ) : (
        <MentionPicker value={people} onChange={(list) => setPeople(list.slice(-1))} />
      )}
      <TextAreaField label={t("reason")} name={`award-reason-${period.id}`} value={reason} maxLength={1000} rows={3} dir="auto" onChange={(e) => setReason(e.target.value)} />
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {saved ? <Alert tone="success" title={t("nominated")} live /> : null}
      <div className="engagement-row">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
          {period.myNomination ? t("changeNomination") : t("nominate")}
        </Button>
      </div>
    </form>
  );
}

function Decide({ period, onDecided }: { period: AwardPeriodView; onDecided: (view: AwardPeriodView) => void }) {
  const t = useTranslations("engagement.awards");
  const locale = useLocale();
  const [nominations, setNominations] = useState<AwardNomineeSummary[] | null>(null);
  const [picked, setPicked] = useState<string[]>([]);
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function load() {
    setBusy("load");
    setMessages([]);
    const result = await call<AwardNomineeSummary[]>(() => fetch(`${engagementApi}/awards/${period.id}/nominations`, { cache: "no-store" }), t("failed"));
    if (result.ok) setNominations(result.value);
    else setMessages(result.messages);
    setBusy(null);
  }

  async function decide() {
    setBusy("decide");
    setMessages([]);
    const result = await call<AwardPeriodView>(() => send(`${engagementApi}/awards/${period.id}/winners`, "POST", { nomineeIds: picked }), t("failed"));
    if (result.ok) onDecided(result.value);
    else setMessages(result.messages);
    setBusy(null);
  }

  return (
    <div className="engagement">
      {nominations === null ? (
        <div className="engagement-row">
          <Button variant="secondary" busy={busy === "load"} busyLabel={t("working")} onClick={() => void load()}>
            {t("review")}
          </Button>
        </div>
      ) : nominations.length === 0 ? (
        <p className="engagement-meta">{t("noNominations")}</p>
      ) : (
        <fieldset className="engagement">
          <legend>{t("chooseWinners")}</legend>
          {nominations.map((n) => (
            <div key={n.nominee.id}>
              <Checkbox
                label={t("nomineeCount", { name: nomineeName(n.nominee, locale), count: n.count })}
                checked={picked.includes(n.nominee.id)}
                onChange={(e) => setPicked((list) => (e.target.checked ? [...list, n.nominee.id] : list.filter((x) => x !== n.nominee.id)))}
              />
              <ul aria-label={t("reasons")}>
                {n.reasons.map((r) => (
                  <li key={`${r.nominator.id}-${r.at}`} className="engagement-meta">
                    <bdi>{r.nominator.displayName}</bdi>: <span dir="auto">{r.reason}</span>
                  </li>
                ))}
              </ul>
            </div>
          ))}
          <div className="engagement-row">
            <Button variant="primary" icon="check" disabled={picked.length === 0} busy={busy === "decide"} busyLabel={t("working")} onClick={() => void decide()}>
              {t("confirmWinners")}
            </Button>
          </div>
        </fieldset>
      )}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
    </div>
  );
}
