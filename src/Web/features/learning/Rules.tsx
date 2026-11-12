"use client";

import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { unitLabel } from "@/lib/admin/types";
import { call, learningApi, send, type AssignmentRule, type CourseDetail } from "@/lib/learning";
import { toggle, useUnits } from "./parts";
import "./learning.css";

const guids = (text: string) => [...new Set(text.match(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi) ?? [])];

/**
 * Assignment rules for a course (spec 028 FR-005 to FR-008): who must take it, due how many days after they are covered,
 * how often it recurs and how long the grace period is. The API checks the scope, the entitlement and the people; this
 * form only collects the choices.
 */
export function Rules({ course }: { course: CourseDetail }) {
  const t = useTranslations("learning.rules");
  const locale = useLocale();
  const format = useFormatter();
  const units = useUnits();
  const [rules, setRules] = useState<AssignmentRule[] | "loading" | string[]>("loading");
  const [scope, setScope] = useState(course.orgUnitId ?? "");
  const [audienceUnits, setAudienceUnits] = useState<string[]>([]);
  const [people, setPeople] = useState("");
  const [dueDays, setDueDays] = useState("30");
  const [recurrence, setRecurrence] = useState("");
  const [grace, setGrace] = useState("0");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const url = `${learningApi}/manage/courses/${course.id}/rules`;

  useEffect(() => {
    let live = true;
    void call<AssignmentRule[]>(() => fetch(url, { cache: "no-store" }), t("failed")).then((r) => {
      if (live) setRules(r.ok ? r.value : r.messages);
    });
    return () => {
      live = false;
    };
  }, [url, t]);

  async function create() {
    setBusy(true);
    setMessages([]);
    setNotice(null);
    const result = await call<AssignmentRule>(
      () =>
        send(url, "POST", {
          orgUnitId: scope || null,
          audience: audienceUnits.length === 0 ? { anyOf: [] } : { anyOf: [{ orgUnits: audienceUnits.map((id) => ({ id, subtree: true })) }] },
          people: guids(people),
          dueDays: Number(dueDays),
          recurrenceMonths: recurrence ? Number(recurrence) : null,
          graceDays: Number(grace || "0"),
        }),
      t("failed"),
    );
    setBusy(false);
    if (!result.ok) {
      setMessages(result.messages);
      return;
    }
    const created = result.value;
    setRules((list) => (Array.isArray(list) && typeof list[0] !== "string" ? [created, ...(list as AssignmentRule[])] : [created]));
    setAudienceUnits([]);
    setPeople("");
    setNotice(t("created", { count: created.covered }));
  }

  async function end(rule: AssignmentRule) {
    setMessages([]);
    const result = await call<AssignmentRule>(() => send(`${url}/${rule.id}:end`, "POST"), t("failed"));
    if (!result.ok) {
      setMessages(result.messages);
      return;
    }
    setRules((list) => (Array.isArray(list) ? (list as AssignmentRule[]).map((r) => (r.id === rule.id ? result.value : r)) : list));
    setNotice(t("ended"));
  }

  if (rules === "loading") return <StateView kind="loading" minBlockSize="8rem" />;
  if (rules.length > 0 && typeof rules[0] === "string") {
    return <StateView kind="error" title={t("failed")} message={(rules as string[]).join(" ")} />;
  }
  const list = rules as AssignmentRule[];
  const unitName = (id: string | null) => {
    const unit = id ? units.find((u) => u.id === id) : undefined;
    return unit ? unitLabel(unit, locale) : t("wholeOrganization");
  };

  return (
    <div className="learning">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {notice ? (
        <Alert tone="success" title={notice} live>
          {t("noticeHint")}
        </Alert>
      ) : null}

      <section className="learning-section" aria-labelledby="rules-list">
        <h2 id="rules-list">{t("title")}</h2>
        <p className="learning-meta">{t("hint")}</p>
        {list.length === 0 ? (
          <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} icon="calendar" />
        ) : (
          <ul className="learning-list" aria-label={t("title")}>
            {list.map((rule) => {
              const clauses = rule.audience.anyOf?.flatMap((c) => c.orgUnits ?? []) ?? [];
              return (
                <li key={rule.id}>
                  <div className="learning-row">
                    <strong>
                      <bdi>{unitName(rule.orgUnitId)}</bdi>
                    </strong>
                    <Badge tone={rule.status === "active" ? "success" : "neutral"}>{t(`status.${rule.status}`)}</Badge>
                    <span className="learning-meta">{t("covered", { count: rule.covered })}</span>
                  </div>
                  <p className="learning-meta">
                    {t("terms", { due: rule.dueDays, grace: rule.graceDays })}{" "}
                    {rule.recurrenceMonths ? t("recurs", { months: rule.recurrenceMonths }) : t("once")}
                  </p>
                  <p className="learning-meta">
                    {clauses.length > 0
                      ? t("units", { names: clauses.map((c) => unitName(c.id)).join(", ") })
                      : rule.people.length > 0
                        ? t("onlyPeople")
                        : t("everyoneInScope")}
                  </p>
                  {rule.people.length > 0 ? (
                    <p className="learning-meta">
                      <bdi>{t("people", { names: rule.people.map((p) => p.displayName).join(", ") })}</bdi>
                    </p>
                  ) : null}
                  <div className="learning-row">
                    <span className="learning-meta">{t("createdOn", { date: format.dateTime(new Date(rule.createdAt), { dateStyle: "medium" }) })}</span>
                    {rule.status === "active" ? (
                      <ConfirmAction
                        label={t("end")}
                        question={t("endQuestion")}
                        confirmLabel={t("endConfirm")}
                        cancelLabel={t("cancel")}
                        onConfirm={() => void end(rule)}
                      />
                    ) : null}
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </section>

      <fieldset className="learning-section" disabled={busy || course.status === "retired"}>
        <legend>{t("new")}</legend>
        <p className="learning-meta">{t("publishFirst")}</p>
        <div className="learning-row">
          <SelectField
            label={t("scope")}
            name="rule-scope"
            value={scope}
            hint={t("scopeHint")}
            onChange={(e) => setScope(e.target.value)}
            options={[{ value: "", label: t("wholeOrganization") }, ...units.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]}
          />
          <FormField label={t("dueDays")} name="rule-due" type="number" min={1} max={365} value={dueDays} hint={t("dueDaysHint")} onChange={(e) => setDueDays(e.target.value)} />
          <FormField
            label={t("recurrence")}
            name="rule-recurrence"
            type="number"
            min={1}
            max={60}
            value={recurrence}
            hint={t("recurrenceHint")}
            onChange={(e) => setRecurrence(e.target.value)}
          />
          <FormField label={t("grace")} name="rule-grace" type="number" min={0} max={30} value={grace} hint={t("graceHint")} onChange={(e) => setGrace(e.target.value)} />
        </div>
        <p className="learning-meta">{t("audienceHint")}</p>
        {units.length > 0 ? (
          <div className="learning-row">
            {units.map((u) => (
              <Checkbox
                key={u.id}
                name="rule-audience"
                label={<bdi>{unitLabel(u, locale)}</bdi>}
                checked={audienceUnits.includes(u.id)}
                onChange={(e) => setAudienceUnits(toggle(audienceUnits, u.id, e.target.checked))}
              />
            ))}
          </div>
        ) : null}
        <TextAreaField label={t("peopleField")} name="rule-people" rows={2} value={people} hint={t("peopleHint")} onChange={(e) => setPeople(e.target.value)} />
        <div className="learning-row">
          <Button variant="primary" icon="plus" busy={busy} disabled={!dueDays} onClick={() => void create()}>
            {t("create")}
          </Button>
        </div>
      </fieldset>
    </div>
  );
}
