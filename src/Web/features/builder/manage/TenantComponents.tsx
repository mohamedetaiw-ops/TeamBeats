"use client";

import { useId, useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { FormField, FormPanel, TextAreaField } from "@/components/ui/FormField";
import { NumberField } from "@/components/ui/Inputs";
import { StateView } from "@/components/ui/StateView";
import { Switch } from "@/components/ui/Switch";
import type { TenantComponent, TenantComponents as View } from "@/lib/experience/builder-types";
import { SendMessages, useSend } from "./useSend";

const CONTEXT = ["locale", "direction", "themeMode", "density", "deviceClass", "pageId", "siteId", "orgUnitId", "tenantId"] as const;
const tone = { in_review: "warning", approved: "success", rejected: "danger" } as const;

/**
 * Components the organization builds itself (spec 032 R3 FR-031). A version is sent for the platform's security review
 * and can be tried here in its sandbox; once a version is approved the component can be turned on for pages. What is
 * allowed, and every check, is the API's.
 */
export function TenantComponents({ initial }: { initial: View }) {
  const t = useTranslations("builder.manage.tenantComponents");
  const locale = useLocale();
  const formId = useId();
  const [view, setView] = useState(initial);
  const [trying, setTrying] = useState<{ id: string; version: number } | null>(null);
  const [id, setId] = useState("");
  const [nameEn, setNameEn] = useState("");
  const [nameAr, setNameAr] = useState("");
  const [hosts, setHosts] = useState("");
  const [events, setEvents] = useState("");
  const [height, setHeight] = useState<number | null>(320);
  const [context, setContext] = useState<string[]>(["locale", "direction"]);
  const [document, setDocument] = useState("");
  const { busy, result, send } = useSend(t("failed"));

  async function reload() {
    const response = await fetch("/api/v1/experience/tenant-components", { cache: "no-store" });
    if (response.ok) setView((await response.json()) as View);
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    const lines = (s: string) => s.split(/[\s,]+/).map((x) => x.trim()).filter(Boolean);
    const done = await send(
      "/api/v1/experience/tenant-components",
      "POST",
      { id, names: { en: nameEn, ar: nameAr }, hosts: lines(hosts), events: lines(events), context, height, document },
      t("submitted"),
    );
    if (done) {
      setDocument("");
      await reload();
    }
  }

  async function readFile(file: File | undefined) {
    if (file) setDocument(await file.text());
  }

  if (!view.available) {
    return <StateView kind="empty" title={t("unavailable")} message={t("unavailableHint")} />;
  }

  const columns: Column<TenantComponent>[] = [
    { key: "name", header: t("component"), rowHeader: true, cell: (c) => <bdi>{c.names[locale] ?? c.names.en ?? c.id}</bdi> },
    { key: "id", header: t("id"), cell: (c) => <bdi dir="ltr">{c.id}</bdi> },
    {
      key: "versions",
      header: t("versions"),
      cell: (c) => (
        <ul className="builder-manage-list">
          {c.versions.map((v) => (
            <li key={v.version} data-testid={`tenant-component-${c.id}-${v.version}`}>
              <Badge tone={tone[v.status]}>{t(`status.${v.status}`, { version: v.version })}</Badge>
              {v.reviewNote ? <span className="field-hint"> <bdi>{v.reviewNote}</bdi></span> : null}{" "}
              <Button size="sm" variant="secondary" onClick={() => setTrying({ id: c.id, version: v.version })} aria-label={t("tryLabel", { version: v.version })}>
                {t("try")}
              </Button>
            </li>
          ))}
        </ul>
      ),
    },
    {
      key: "enabled",
      header: t("enabled"),
      align: "end",
      cell: (c) => (
        <Switch
          label={t("enabledLabel", { name: c.names[locale] ?? c.id })}
          checked={c.enabled}
          disabled={busy || (c.live === null && !c.enabled)}
          onChange={async (e) => {
            if (await send(`/api/v1/experience/tenant-components/${encodeURIComponent(c.id)}/enablement`, "PUT", { enabled: e.target.checked }, t("changed"))) {
              await reload();
            }
          }}
        />
      ),
    },
  ];

  return (
    <div className="builder-manage" data-testid="tenant-components">
      <DataTable
        caption={t("caption")}
        columns={columns}
        rows={view.components}
        rowKey={(c) => c.id}
        emptyTitle={t("emptyTitle")}
        emptyMessage={t("empty")}
        density="compact"
      />
      {trying ? (
        <section className="builder-manage-form" aria-label={t("tryTitle", { id: trying.id, version: trying.version })}>
          <h2>{t("tryTitle", { id: trying.id, version: trying.version })}</h2>
          <p className="field-hint">{t("tryHint")}</p>
          <iframe
            className="builder-sandbox-try"
            title={t("tryTitle", { id: trying.id, version: trying.version })}
            src={`/api/v1/experience/sandbox/${encodeURIComponent(trying.id)}/${trying.version}`}
            sandbox="allow-scripts"
            referrerPolicy="no-referrer"
          />
          <Button size="sm" variant="secondary" onClick={() => setTrying(null)}>
            {t("closeTry")}
          </Button>
        </section>
      ) : null}
      <FormPanel as="form" aria-labelledby={formId} onSubmit={(e) => void submit(e)} noValidate>
        <h2 id={formId}>{t("newTitle")}</h2>
        <p className="field-hint">{t("newHint")}</p>
        <FormField label={t("idField")} name="id" hint={t("idHint")} value={id} dir="ltr" autoComplete="off" onChange={(e) => setId(e.target.value.trim().toLowerCase())} />
        <FormField label={t("nameEn")} name="nameEn" value={nameEn} dir="ltr" onChange={(e) => setNameEn(e.target.value)} />
        <FormField label={t("nameAr")} name="nameAr" value={nameAr} dir="rtl" onChange={(e) => setNameAr(e.target.value)} />
        <TextAreaField label={t("hosts")} name="hosts" hint={t("hostsHint")} value={hosts} dir="ltr" rows={2} onChange={(e) => setHosts(e.target.value)} />
        <fieldset className="builder-manage-checks">
          <legend>{t("context")}</legend>
          {CONTEXT.map((k) => (
            <Checkbox
              key={k}
              label={t(`contextKey.${k}`)}
              checked={context.includes(k)}
              onChange={(e) => setContext((c) => (e.target.checked ? [...c, k] : c.filter((x) => x !== k)))}
            />
          ))}
        </fieldset>
        <FormField label={t("events")} name="events" hint={t("eventsHint")} value={events} dir="ltr" onChange={(e) => setEvents(e.target.value)} />
        <NumberField label={t("height")} name="height" value={height} onValueChange={setHeight} min={80} max={1200} step={20} unit={t("pixels")} />
        <FormField label={t("file")} name="file" type="file" accept=".html,text/html" hint={t("fileHint")} onChange={(e) => void readFile(e.target.files?.[0])} />
        <TextAreaField label={t("document")} name="document" value={document} dir="ltr" rows={8} spellCheck={false} onChange={(e) => setDocument(e.target.value)} />
        <div className="builder-actions">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("submitting")}>
            {t("submit")}
          </Button>
        </div>
      </FormPanel>
      <SendMessages result={result} />
    </div>
  );
}
