"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { Switch } from "@/components/ui/Switch";
import { Widget } from "@/components/ui/Widget";
import { problemMessages } from "@/lib/notifications/types";
import type { SkillListView } from "@/lib/people/skills";
import "./people.css";

async function call(url: string, method: string, body?: unknown, ifMatch?: string): Promise<Response> {
  const headers: Record<string, string> = {};
  if (body !== undefined) headers["content-type"] = "application/json";
  if (ifMatch) headers["if-match"] = ifMatch;
  return fetch(url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
}

type ListDraft = { id: string | null; en: string; ar: string; status: "active" | "suggested"; people: number; approve: boolean };

/**
 * The tenant's skill list and settings for people administrators (spec 010 R2 US6, FR-031): turn skills on or off,
 * allow free text, rename, add or remove skills, approve or merge suggestions.
 */
export function SkillListAdmin({ initial }: { initial: SkillListView }) {
  const t = useTranslations("people.skills.admin");
  const [view, setView] = useState(initial);
  const [enabled, setEnabled] = useState(initial.enabled);
  const [free, setFree] = useState(initial.allowFreeText);
  const toDrafts = (v: SkillListView): ListDraft[] =>
    v.skills.map((s) => ({ id: s.id, en: s.names.en ?? "", ar: s.names.ar ?? "", status: s.status, people: s.people, approve: false }));
  const [drafts, setDrafts] = useState<ListDraft[]>(toDrafts(initial));
  const [merge, setMerge] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  if (!view.available) return null;

  function applied(next: SkillListView, eTag: string | null) {
    setView({ ...next, eTag: eTag ?? next.eTag });
    setEnabled(next.enabled);
    setFree(next.allowFreeText);
    setDrafts(toDrafts(next));
    setSaved(true);
  }

  async function save() {
    setBusy(true);
    setMessages([]);
    setSaved(false);
    try {
      const response = await call(
        "/api/v1/people/skills",
        "PUT",
        {
          enabled,
          allowFreeText: free,
          skills: drafts.map((d) => ({
            id: d.id,
            names: Object.fromEntries(Object.entries({ en: d.en, ar: d.ar }).filter(([, v]) => v.trim())),
            approve: d.approve,
          })),
        },
        view.eTag,
      );
      if (response.ok) applied((await response.json()) as SkillListView, response.headers.get("etag"));
      else setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  async function mergeInto(id: string) {
    const into = merge[id];
    if (!into) return;
    setMessages([]);
    const response = await call(`/api/v1/people/skills/${encodeURIComponent(id)}:merge`, "POST", { intoId: into });
    if (response.ok) {
      const list = await call("/api/v1/people/skills", "GET");
      if (list.ok) applied((await list.json()) as SkillListView, list.headers.get("etag"));
    } else {
      setMessages(await problemMessages(response));
    }
  }

  const active = drafts.filter((d) => d.status === "active" && d.id);
  return (
    <Widget title={t("title")}>
      <div className="people-form" data-testid="skill-admin">
        <Switch label={t("enabled")} hint={t("enabledHint")} checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        <Switch label={t("freeText")} hint={t("freeTextHint")} checked={free} onChange={(e) => setFree(e.target.checked)} />
        {drafts.length === 0 ? <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} icon="star" /> : null}
        <ul className="people-skill-list">
          {drafts.map((d, i) => (
            <li key={d.id ?? `new-${i}`}>
              <FormField label={t("nameEn")} name={`skill-en-${i}`} value={d.en} maxLength={80} dir="ltr" onChange={(e) => setDrafts((all) => all.map((x, j) => (j === i ? { ...x, en: e.target.value } : x)))} />
              <FormField label={t("nameAr")} name={`skill-ar-${i}`} value={d.ar} maxLength={80} dir="rtl" onChange={(e) => setDrafts((all) => all.map((x, j) => (j === i ? { ...x, ar: e.target.value } : x)))} />
              <span className="people-meta">{t("people", { count: d.people })}</span>
              {d.status === "suggested" ? (
                <>
                  <Badge tone="warning">{t("suggested")}</Badge>
                  <Switch label={t("approve")} checked={d.approve} onChange={(e) => setDrafts((all) => all.map((x, j) => (j === i ? { ...x, approve: e.target.checked } : x)))} />
                  {active.length > 0 && d.id ? (
                    <>
                      <SelectField
                        label={t("mergeInto")}
                        name={`merge-${i}`}
                        options={[{ value: "", label: t("choose") }, ...active.map((a) => ({ value: a.id!, label: a.en || a.ar }))]}
                        value={merge[d.id] ?? ""}
                        onChange={(e) => setMerge((m) => ({ ...m, [d.id!]: e.target.value }))}
                      />
                      <Button size="sm" variant="secondary" disabled={!merge[d.id]} onClick={() => void mergeInto(d.id!)}>
                        {t("merge")}
                      </Button>
                    </>
                  ) : null}
                </>
              ) : null}
              <Button size="sm" variant="quiet" icon="trash" aria-label={t("removeLabel", { skill: d.en || d.ar })} onClick={() => setDrafts((all) => all.filter((_, j) => j !== i))}>
                {t("remove")}
              </Button>
            </li>
          ))}
        </ul>
        <div className="people-actions">
          <Button
            size="sm"
            variant="secondary"
            icon="plus"
            onClick={() => setDrafts((all) => [...all, { id: null, en: "", ar: "", status: "active", people: 0, approve: false }])}
          >
            {t("add")}
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
      </div>
    </Widget>
  );
}
