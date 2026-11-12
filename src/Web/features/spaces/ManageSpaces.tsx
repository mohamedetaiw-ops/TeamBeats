"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { call, send, spacesApi } from "@/lib/spaces";
import "./spaces.css";

export type SpaceRules = { creationUnits: string[]; guestsAllowed: boolean; guestDays: number; version: number; guestConfidential: boolean };
type TemplateDefinition = {
  lists: { name: string; columns: { name: string; done: boolean }[] }[];
  tasks: { title: string; list: number; column: number }[];
};
export type TemplateView = {
  id: string;
  name: string;
  kind: "project" | "team";
  definition: TemplateDefinition;
  status: "active" | "retired";
  version: number;
  updatedAt: string;
};

/** One list in the editor: columns and starter tasks are one per line. */
type DraftList = { name: string; columns: string; tasks: string };
type Draft = { id: string | null; version: number; name: string; kind: "project" | "team"; lists: DraftList[] };

const lines = (text: string) => text.split("\n").map((l) => l.trim()).filter((l) => l.length > 0);

function toDraft(template: TemplateView): Draft {
  return {
    id: template.id,
    version: template.version,
    name: template.name,
    kind: template.kind,
    lists: template.definition.lists.map((list, i) => ({
      name: list.name,
      columns: list.columns.map((c) => c.name).join("\n"),
      tasks: template.definition.tasks.filter((task) => task.list === i).map((task) => task.title).join("\n"),
    })),
  };
}

/** The editor's lists as a template definition: the last column of a list is its done column, starter tasks go in the first. */
function toDefinition(draft: Draft): TemplateDefinition {
  return {
    lists: draft.lists.map((list) => {
      const columns = lines(list.columns);
      return { name: list.name.trim(), columns: columns.map((name, i) => ({ name, done: i === columns.length - 1 })) };
    }),
    tasks: draft.lists.flatMap((list, i) => lines(list.tasks).map((title) => ({ title, list: i, column: 0 }))),
  };
}

/**
 * The organization's space rules and templates (spec 029 FR-012, FR-017) for spaces administrators: whether guests
 * may be invited and for how long, and which tenant templates creators can pick. The API validates every value.
 */
export function ManageSpaces({ initialRules, initialTemplates, guestsInPlan }: { initialRules: SpaceRules; initialTemplates: TemplateView[]; guestsInPlan: boolean }) {
  const t = useTranslations("spaces.manage");
  const [rules, setRules] = useState(initialRules);
  const [guests, setGuests] = useState(initialRules.guestsAllowed);
  const [days, setDays] = useState(String(initialRules.guestDays));
  const [confidential, setConfidential] = useState(initialRules.guestConfidential);
  const [templates, setTemplates] = useState(initialTemplates);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);
  const [draft, setDraft] = useState<Draft | null>(null);

  async function save() {
    setSaved(false);
    const result = await call<SpaceRules>(
      () => send(`${spacesApi}/manage/settings`, "PUT", {
        creationUnits: rules.creationUnits, guestsAllowed: guests, guestDays: Number(days), guestConfidential: confidential, version: rules.version,
      }),
      t("failed"),
    );
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setRules(result.value);
    setSaved(true);
  }

  function blank(): Draft {
    return { id: null, version: 0, name: "", kind: "project", lists: [{ name: t("editor.defaultList"), columns: t("editor.defaultColumns"), tasks: "" }] };
  }

  function setList(index: number, change: Partial<DraftList>) {
    setDraft((d) => (d ? { ...d, lists: d.lists.map((l, i) => (i === index ? { ...l, ...change } : l)) } : d));
  }

  async function saveTemplate(current: Draft) {
    setSaved(false);
    const body = { name: current.name.trim(), kind: current.kind, definition: toDefinition(current), version: current.version };
    const result = await call<TemplateView>(
      () => (current.id ? send(`${spacesApi}/manage/templates/${current.id}`, "PUT", body) : send(`${spacesApi}/manage/templates`, "POST", body)),
      t("failed"),
    );
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setTemplates((list) => (current.id ? list.map((x) => (x.id === current.id ? result.value : x)) : [...list, result.value]));
    setDraft(null);
    setSaved(true);
  }

  async function retire(template: TemplateView) {
    const result = await call<TemplateView>(() => send(`${spacesApi}/manage/templates/${template.id}:retire`, "POST"), t("failed"));
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setTemplates((list) => list.map((x) => (x.id === template.id ? result.value : x)));
  }

  return (
    <div className="spaces">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {saved ? <Alert tone="success" title={t("saved")} live /> : null}
      <section className="spaces-section" aria-labelledby="spaces-rules">
        <h2 id="spaces-rules">{t("rulesTitle")}</h2>
        <p className="spaces-meta">{rules.creationUnits.length === 0 ? t("everyoneCreates") : t("unitsCreate", { count: rules.creationUnits.length })}</p>
        <form
          className="spaces-form"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <Checkbox label={t("guests")} hint={guestsInPlan ? t("guestsHint") : t("guestsNotInPlan")} checked={guests} disabled={!guestsInPlan}
            onChange={(e) => setGuests(e.target.checked)} />
          <FormField label={t("guestDays")} hint={t("guestDaysHint")} name="guestDays" type="number" min={7} max={365} value={days} onChange={(e) => setDays(e.target.value)} />
          <Checkbox label={t("guestConfidential")} hint={t("guestConfidentialHint")} checked={confidential} disabled={!guestsInPlan}
            onChange={(e) => setConfidential(e.target.checked)} />
          <div className="spaces-row">
            <Button type="submit" variant="primary">
              {t("save")}
            </Button>
          </div>
        </form>
      </section>
      <section className="spaces-section" aria-labelledby="spaces-templates">
        <h2 id="spaces-templates">{t("templatesTitle")}</h2>
        <p className="spaces-meta">{t("templatesHint")}</p>
        {templates.length === 0 ? (
          <StateView kind="empty" title={t("noTemplatesTitle")} message={t("noTemplates")} icon="folder" />
        ) : (
          <ul className="spaces-list" aria-label={t("templatesTitle")}>
            {templates.map((template) => (
              <li key={template.id} className="spaces-row">
                <bdi>{template.name}</bdi>
                <Badge>{t(`kind.${template.kind}`)}</Badge>
                {template.status === "retired" ? (
                  <Badge tone="neutral">{t("retired")}</Badge>
                ) : (
                  <>
                    <Button size="sm" variant="quiet" onClick={() => setDraft(toDraft(template))}>
                      {t("editor.edit", { name: template.name })}
                    </Button>
                    <Button size="sm" variant="quiet" onClick={() => void retire(template)}>
                      {t("retire")}
                    </Button>
                  </>
                )}
              </li>
            ))}
          </ul>
        )}
        {draft ? (
          <form
            className="spaces-form"
            aria-labelledby="template-editor"
            onSubmit={(e) => {
              e.preventDefault();
              void saveTemplate(draft);
            }}
          >
            <h3 id="template-editor">{draft.id ? t("editor.editTitle") : t("editor.newTitle")}</h3>
            <FormField label={t("editor.name")} name="templateName" value={draft.name} maxLength={120} required
              onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
            <SelectField label={t("editor.kind")} name="templateKind" value={draft.kind}
              onChange={(e) => setDraft({ ...draft, kind: e.target.value === "team" ? "team" : "project" })}
              options={[{ value: "project", label: t("kind.project") }, { value: "team", label: t("kind.team") }]} />
            {draft.lists.map((list, i) => (
              <fieldset key={i} className="spaces-card">
                <legend>{t("editor.listLegend", { number: i + 1 })}</legend>
                <FormField label={t("editor.listName")} name={`list-${i}`} value={list.name} maxLength={80} required onChange={(e) => setList(i, { name: e.target.value })} />
                <TextAreaField label={t("editor.columns")} hint={t("editor.columnsHint")} name={`columns-${i}`} value={list.columns} rows={4}
                  onChange={(e) => setList(i, { columns: e.target.value })} />
                <TextAreaField label={t("editor.tasks")} hint={t("editor.tasksHint")} name={`tasks-${i}`} value={list.tasks} rows={4}
                  onChange={(e) => setList(i, { tasks: e.target.value })} />
                {draft.lists.length > 1 ? (
                  <Button size="sm" variant="quiet" onClick={() => setDraft({ ...draft, lists: draft.lists.filter((_, x) => x !== i) })}>
                    {t("editor.removeList", { number: i + 1 })}
                  </Button>
                ) : null}
              </fieldset>
            ))}
            <div className="spaces-row">
              <Button icon="plus" onClick={() => setDraft({ ...draft, lists: [...draft.lists, { name: "", columns: t("editor.defaultColumns"), tasks: "" }] })}>
                {t("editor.addList")}
              </Button>
              <Button type="submit" variant="primary">
                {t("editor.save")}
              </Button>
              <Button variant="quiet" onClick={() => setDraft(null)}>
                {t("editor.cancel")}
              </Button>
            </div>
          </form>
        ) : (
          <div className="spaces-row">
            <Button icon="plus" onClick={() => setDraft(blank())}>
              {t("editor.new")}
            </Button>
          </div>
        )}
      </section>
    </div>
  );
}
