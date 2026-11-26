"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import { problemMessages } from "@/lib/notifications/types";
import { localName } from "@/lib/people/types";
import {
  proficiencies,
  type OwnSkillsView,
  type PersonSkillsView,
  type Proficiency,
} from "@/lib/people/skills";
import { PersonItem } from "./PersonItem";
import "./people.css";

async function call(url: string, method: string, body?: unknown, ifMatch?: string): Promise<Response> {
  const headers: Record<string, string> = {};
  if (body !== undefined) headers["content-type"] = "application/json";
  if (ifMatch) headers["if-match"] = ifMatch;
  return fetch(url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
}

const tones: Record<Proficiency, "accent" | "success" | "neutral"> = { expert: "accent", good: "success", basic: "neutral" };

/**
 * A person's skills on their profile (spec 010 R2 US6 scenario 1 and 2): proficiency and the endorsements the owner
 * did not hide. A colleague endorses or takes back an endorsement; the API refuses endorsing oneself.
 */
export function PersonSkills({ personId, initial }: { personId: string; initial: PersonSkillsView }) {
  const t = useTranslations("people.skills");
  const locale = useLocale();
  const [view, setView] = useState(initial);
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function endorse(skillId: string, on: boolean) {
    setBusy(skillId);
    setMessages([]);
    try {
      const response = await call(`/api/v1/people/${encodeURIComponent(personId)}/skills/${encodeURIComponent(skillId)}/endorsement`, on ? "PUT" : "DELETE");
      if (response.ok) setView((await response.json()) as PersonSkillsView);
      else setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  if (!view.enabled || view.items.length === 0) return null;
  return (
    <Widget title={t("title")}>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <ul className="people-skills" data-testid="person-skills">
        {view.items.map((s) => {
          const name = localName(s.names, locale);
          return (
            <li key={s.skillId}>
              <bdi className="people-skill-name">{name}</bdi>
              <Badge tone={tones[s.proficiency]}>{t(`level.${s.proficiency}`)}</Badge>
              {s.suggested ? <Badge tone="warning">{t("suggested")}</Badge> : null}
              <span className="people-meta">{t("endorsements", { count: s.endorsements })}</span>
              {s.canEndorse ? (
                <Button
                  size="sm"
                  variant="quiet"
                  icon={s.endorsedByMe ? "check" : "plus"}
                  aria-pressed={s.endorsedByMe}
                  aria-label={s.endorsedByMe ? t("unendorseLabel", { skill: name }) : t("endorseLabel", { skill: name })}
                  busy={busy === s.skillId}
                  busyLabel={t("working")}
                  onClick={() => void endorse(s.skillId, !s.endorsedByMe)}
                >
                  {s.endorsedByMe ? t("endorsed") : t("endorse")}
                </Button>
              ) : null}
            </li>
          );
        })}
      </ul>
    </Widget>
  );
}

type Draft = { skillId: string | null; name: string; names: Record<string, string>; proficiency: Proficiency };

/**
 * The signed-in person's own skills (spec 010 R2 US6): choose from the tenant's list, or type a new one when the tenant
 * allows it, with a proficiency; see who endorsed each and hide an endorsement.
 */
export function OwnSkillsEditor({ initial }: { initial: OwnSkillsView }) {
  const t = useTranslations("people.skills");
  const locale = useLocale();
  const [view, setView] = useState(initial);
  const [drafts, setDrafts] = useState<Draft[]>(initial.items.map((s) => ({ skillId: s.skillId, name: "", names: s.names, proficiency: s.proficiency })));
  const [pick, setPick] = useState("");
  const [typed, setTyped] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  if (!view.enabled) return null;
  const available = view.choices.filter((c) => !drafts.some((d) => d.skillId === c.id));

  function add() {
    if (pick) {
      const choice = view.choices.find((c) => c.id === pick);
      if (choice) setDrafts((all) => [...all, { skillId: choice.id, name: "", names: choice.names, proficiency: "good" }]);
      setPick("");
    } else if (typed.trim()) {
      setDrafts((all) => [...all, { skillId: null, name: typed.trim(), names: {}, proficiency: "good" }]);
      setTyped("");
    }
  }

  async function save() {
    setBusy(true);
    setMessages([]);
    setSaved(false);
    try {
      const response = await call("/api/v1/me/skills", "PUT", {
        skills: drafts.map((d) => ({ skillId: d.skillId, name: d.skillId ? null : d.name, proficiency: d.proficiency })),
      });
      if (response.ok) {
        const next = (await response.json()) as OwnSkillsView;
        setView(next);
        setDrafts(next.items.map((s) => ({ skillId: s.skillId, name: "", names: s.names, proficiency: s.proficiency })));
        setSaved(true);
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  async function hide(skillId: string, endorserId: string, hidden: boolean) {
    setMessages([]);
    const response = await call(`/api/v1/me/skills/${encodeURIComponent(skillId)}/endorsements/${encodeURIComponent(endorserId)}:${hidden ? "hide" : "show"}`, "POST");
    if (response.ok) setView((await response.json()) as OwnSkillsView);
    else setMessages(await problemMessages(response));
  }

  const levelOptions = proficiencies.map((p) => ({ value: p, label: t(`level.${p}`) }));
  return (
    <Widget title={t("ownTitle")}>
      <p className="people-meta">{t("ownIntro")}</p>
      {drafts.length === 0 ? <StateView kind="empty" title={t("noneTitle")} message={t("none")} icon="star" /> : null}
      <ul className="people-skills" data-testid="own-skills">
        {drafts.map((d, i) => {
          const name = d.skillId ? localName(d.names, locale) : d.name;
          const endorsers = view.items.find((s) => s.skillId === d.skillId)?.endorsers ?? [];
          return (
            <li key={d.skillId ?? `new-${i}`}>
              <bdi className="people-skill-name">{name}</bdi>
              <SelectField
                label={t("levelFor", { skill: name })}
                name={`proficiency-${i}`}
                options={levelOptions}
                value={d.proficiency}
                onChange={(e) => setDrafts((all) => all.map((x, j) => (j === i ? { ...x, proficiency: e.target.value as Proficiency } : x)))}
              />
              <Button size="sm" variant="quiet" icon="trash" aria-label={t("removeLabel", { skill: name })} onClick={() => setDrafts((all) => all.filter((_, j) => j !== i))}>
                {t("remove")}
              </Button>
              {endorsers.length > 0 ? (
                <ul className="people-endorsers" aria-label={t("endorsersLabel", { skill: name })}>
                  {endorsers.map((e) => (
                    <li key={e.id}>
                      <bdi>{e.displayName}</bdi>
                      {e.hidden ? <Badge tone="neutral">{t("hidden")}</Badge> : null}
                      <Button size="sm" variant="quiet" icon={e.hidden ? "eye" : "close"} onClick={() => void hide(d.skillId!, e.id, !e.hidden)}>
                        {e.hidden ? t("show") : t("hide")}
                      </Button>
                    </li>
                  ))}
                </ul>
              ) : null}
            </li>
          );
        })}
      </ul>
      <div className="people-skill-add">
        {available.length > 0 ? (
          <SelectField
            label={t("add")}
            name="add-skill"
            options={[{ value: "", label: t("choose") }, ...available.map((c) => ({ value: c.id, label: localName(c.names, locale) }))]}
            value={pick}
            onChange={(e) => setPick(e.target.value)}
          />
        ) : null}
        {view.allowFreeText ? <FormField label={t("typeNew")} name="new-skill" value={typed} maxLength={80} dir="auto" onChange={(e) => setTyped(e.target.value)} /> : null}
        <Button size="sm" variant="secondary" icon="plus" disabled={!pick && !typed.trim()} onClick={add}>
          {t("addButton")}
        </Button>
      </div>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {saved ? <Alert tone="success" title={t("saved")} live /> : null}
      <div className="people-actions">
        <Button variant="primary" busy={busy} busyLabel={t("working")} onClick={() => void save()}>
          {t("save")}
        </Button>
      </div>
    </Widget>
  );
}
