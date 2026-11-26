"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Widget } from "@/components/ui/Widget";
import { problemMessages } from "@/lib/notifications/types";
import type { PersonCard, PersonView } from "@/lib/people/types";
import { PersonPicker } from "@/features/services/PersonPicker";
import { PersonItem } from "./PersonItem";
import "./people.css";

/**
 * Who works with the person (spec 010 R2 US8 scenarios 2 and 3): their assistants, whom they assist, and while a spec
 * 016 delegation is active, who their approvals go to and until when. People administrators change the assistants.
 */
export function Relationships({ person }: { person: PersonView }) {
  const t = useTranslations("people.relations");
  const locale = useLocale();
  const [view, setView] = useState(person);
  const [editing, setEditing] = useState<(string | null)[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const assistants = view.assistants ?? [];
  const assistantOf = view.assistantOf ?? [];
  if (
    assistants.length === 0 &&
    assistantOf.length === 0 &&
    !view.canSetManager
  )
    return null;

  async function save() {
    if (!editing) return;
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(
        `/api/v1/people/${encodeURIComponent(view.id)}/assistants`,
        {
          method: "PUT",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({
            assistantIds: editing.filter((id): id is string => !!id),
          }),
        },
      );
      if (response.ok) {
        setView((await response.json()) as PersonView);
        setEditing(null);
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  const list = (people: PersonCard[], testId: string) => (
    <ul className="people-relations" data-testid={testId}>
      {people.map((p) => (
        <li key={p.id}>
          <PersonItem person={p} locale={locale} />
        </li>
      ))}
    </ul>
  );

  return (
    <Widget title={t("title")}>
      {assistants.length > 0 ? (
        <>
          <h3 className="people-relations-head">{t("assistants")}</h3>
          {list(assistants, "person-assistants")}
        </>
      ) : null}
      {assistantOf.length > 0 ? (
        <>
          <h3 className="people-relations-head">{t("assistantOf")}</h3>
          {list(assistantOf, "person-assistant-of")}
        </>
      ) : null}
      {view.canSetManager && !editing ? (
        <div className="people-actions">
          <Button
            size="sm"
            variant="secondary"
            icon="edit"
            onClick={() =>
              setEditing([...assistants.map((a) => a.id), null].slice(0, 3))
            }
          >
            {t("edit")}
          </Button>
        </div>
      ) : null}
      {editing ? (
        <div className="people-form">
          {editing.map((id, i) => (
            <PersonPicker
              key={i}
              label={t("assistantN", { n: String(i + 1) })}
              value={id}
              onChange={(next) =>
                setEditing((all) => {
                  const copy = [...(all ?? [])];
                  copy[i] = next;
                  return copy.length < 3 && copy.every(Boolean)
                    ? [...copy, null]
                    : copy;
                })
              }
            />
          ))}
          {messages.length > 0 ? (
            <Alert tone="danger" title={t("failed")} live>
              {messages.join(" ")}
            </Alert>
          ) : null}
          <div className="people-actions">
            <Button
              variant="primary"
              busy={busy}
              busyLabel={t("working")}
              onClick={() => void save()}
            >
              {t("save")}
            </Button>
            <Button variant="quiet" onClick={() => setEditing(null)}>
              {t("cancel")}
            </Button>
          </div>
        </div>
      ) : null}
    </Widget>
  );
}
