"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { Switch } from "@/components/ui/Switch";
import { Widget } from "@/components/ui/Widget";
import { problemMessages } from "@/lib/notifications/types";
import { localName } from "@/lib/people/types";
import {
  announceOwnSkillsChanged,
  proficiencies,
  type Proficiency,
  type SkillSuggestionRun,
  type SkillSuggestionsView,
} from "@/lib/people/skills";
import "./people.css";

const URL = "/api/v1/me/skills/suggestions";

async function send(url: string, method: string, body?: unknown): Promise<Response> {
  return fetch(url, {
    method,
    headers: body === undefined ? undefined : { "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

/**
 * Skills suggested from what the person published (spec 010 R3 US9, FR-033 to FR-035). Off until the person turns it
 * on; a run happens only when they ask. Each suggestion names the items it came from and is marked as AI generated.
 */
export function SkillSuggestions({ initial }: { initial: SkillSuggestionsView }) {
  const t = useTranslations("people.suggestions");
  const levels = useTranslations("people.skills");
  const locale = useLocale();
  const [view, setView] = useState(initial);
  const [levelOf, setLevelOf] = useState<Record<string, Proficiency>>({});
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<{ tone: "info" | "warning" | "danger"; text: string } | null>(null);

  if (!view.available) return null;

  async function act(run: () => Promise<Response>, after?: (r: Response) => Promise<void>) {
    setBusy(true);
    setNote(null);
    try {
      const response = await run();
      if (response.ok) {
        if (after) await after(response);
        else setView((await response.json()) as SkillSuggestionsView);
      } else {
        setNote({ tone: "danger", text: (await problemMessages(response)).join(" ") || t("failed") });
      }
    } catch {
      setNote({ tone: "danger", text: t("failed") });
    } finally {
      setBusy(false);
    }
  }

  const ask = () =>
    act(
      () => send(`${URL}:run`, "POST"),
      async (response) => {
        const run = (await response.json()) as SkillSuggestionRun;
        setView(run.suggestions);
        if (run.outcome !== "suggested")
          setNote({ tone: run.outcome === "policy_blocked" || run.outcome === "unavailable" ? "warning" : "info", text: t(`outcome.${run.outcome}`) });
      },
    );

  const accept = (skillId: string) =>
    act(
      () => send(`${URL}/${encodeURIComponent(skillId)}:accept`, "POST", { proficiency: levelOf[skillId] ?? "good" }),
      async (response) => {
        setView((await response.json()) as SkillSuggestionsView);
        announceOwnSkillsChanged();
      },
    );

  const levelOptions = proficiencies.map((p) => ({ value: p, label: levels(`level.${p}`) }));
  return (
    <div id="skill-suggestions">
      <Widget title={t("title")}>
        <p className="people-meta">{t("intro")}</p>
        <Switch label={t("consent")} hint={t("consentHint")} checked={view.on} disabled={busy} onChange={(e) => void act(() => send(URL, "PUT", { on: e.target.checked }))} />
        {note ? (
          <Alert tone={note.tone} title={note.text} live />
        ) : null}
        {view.on ? (
          <>
            <div className="people-actions">
              <Button icon="spark" variant="secondary" size="sm" busy={busy} busyLabel={t("running")} disabled={view.runsLeft === 0} onClick={() => void ask()}>
                {t("run")}
              </Button>
              <span className="people-meta">{t("runsLeft", { count: view.runsLeft })}</span>
            </div>
            {view.items.length === 0 ? (
              <StateView kind="empty" title={t("noneTitle")} message={t("none")} icon="spark" />
            ) : (
              <ul className="people-skills" data-testid="skill-suggestions">
                {view.items.map((s) => {
                  const name = localName(s.names, locale);
                  return (
                    <li key={s.skillId}>
                      <bdi className="people-skill-name">{name}</bdi>
                      <Badge tone="info" icon="spark">
                        {t("aiGenerated")}
                      </Badge>
                      <ul className="people-suggestion-from" aria-label={t("fromFor", { skill: name })}>
                        {s.from.map((f) => (
                          <li key={`${f.source}:${f.id}`}>
                            <ButtonLink href={f.link} size="sm" variant="quiet">
                              <bdi>{f.title}</bdi>
                            </ButtonLink>
                          </li>
                        ))}
                      </ul>
                      <SelectField
                        label={t("levelFor", { skill: name })}
                        name={`suggestion-${s.skillId}`}
                        options={levelOptions}
                        value={levelOf[s.skillId] ?? "good"}
                        onChange={(e) => setLevelOf((all) => ({ ...all, [s.skillId]: e.target.value as Proficiency }))}
                      />
                      <div className="people-actions">
                        <Button size="sm" variant="primary" disabled={busy} onClick={() => void accept(s.skillId)} aria-label={t("addFor", { skill: name })}>
                          {t("add")}
                        </Button>
                        <Button
                          size="sm"
                          variant="quiet"
                          disabled={busy}
                          onClick={() => void act(() => send(`${URL}/${encodeURIComponent(s.skillId)}:dismiss`, "POST"))}
                          aria-label={t("dismissFor", { skill: name })}
                        >
                          {t("dismiss")}
                        </Button>
                      </div>
                    </li>
                  );
                })}
              </ul>
            )}
          </>
        ) : null}
      </Widget>
    </div>
  );
}
