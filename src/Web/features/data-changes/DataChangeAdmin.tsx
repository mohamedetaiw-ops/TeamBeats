"use client";

import Link from "next/link";
import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { DataTable } from "@/components/ui/DataTable";
import { problemMessages } from "@/lib/notifications/types";
import { dataChangeTone, type DataChange, type DataChangeSettings } from "@/lib/data-changes";
import "./data-changes.css";

/**
 * Data change settings and the changes of the people HR covers (spec 022 FR-019): which groups people may ask to
 * change and which need a supporting document. Only HR officers of the whole organization change the settings; the
 * API decides and validates.
 */
export function DataChangeAdmin({ settings: initial, changes }: { settings: DataChangeSettings; changes: DataChange[] }) {
  const t = useTranslations("data_changes");
  const ta = useTranslations("data_changes.admin");
  const format = useFormatter();
  const [settings, setSettings] = useState(initial);
  const [draft, setDraft] = useState(() => Object.fromEntries(initial.groups.map((g) => [g.key, { enabled: g.enabled, document: g.document }])));
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function save() {
    setBusy(true);
    setNotice(null);
    setMessages([]);
    try {
      const response = await fetch("/api/v1/hr/data-change-settings", {
        method: "PUT",
        headers: { "content-type": "application/json", "if-match": settings.eTag },
        body: JSON.stringify({ groups: draft }),
      });
      if (!response.ok) {
        const found = await problemMessages(response);
        setMessages(found.length > 0 ? found : [ta("failed")]);
        return;
      }
      setSettings((await response.json()) as DataChangeSettings);
      setNotice(ta("saved"));
    } catch {
      setMessages([ta("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="data-changes">
      {notice ? (
        <p className="notice" role="status">
          {notice}
        </p>
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={ta("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <section className="data-changes-section" aria-labelledby="data-changes-settings">
        <h2 id="data-changes-settings">{ta("settings")}</h2>
        {!settings.canChange ? <p className="field-hint">{ta("wholeOrganizationOnly")}</p> : null}
        {settings.groups.map((g) => (
          <fieldset className="data-changes-group" key={g.key} disabled={!settings.canChange}>
            <legend>{t(`groups.${g.key}`)}</legend>
            <Checkbox
              label={ta("offered")}
              name={`group-${g.key}-enabled`}
              checked={draft[g.key]?.enabled ?? true}
              onChange={(e) => setDraft((all) => ({ ...all, [g.key]: { ...all[g.key]!, enabled: e.target.checked } }))}
            />
            <Checkbox
              label={ta("needsDocument")}
              name={`group-${g.key}-document`}
              checked={draft[g.key]?.document ?? false}
              onChange={(e) => setDraft((all) => ({ ...all, [g.key]: { ...all[g.key]!, document: e.target.checked } }))}
            />
          </fieldset>
        ))}
        {settings.canChange ? (
          <div className="data-changes-row">
            <Button variant="primary" busy={busy} busyLabel={ta("saving")} onClick={() => void save()}>
              {ta("save")}
            </Button>
          </div>
        ) : null}
      </section>
      <section className="data-changes-section" aria-labelledby="data-changes-list">
        <h2 id="data-changes-list">{ta("changes")}</h2>
        <DataTable<DataChange>
          caption={ta("caption")}
          rows={changes}
          rowKey={(r) => r.id}
          density="compact"
          emptyTitle={ta("empty")}
          columns={[
            {
              key: "group",
              header: t("group"),
              rowHeader: true,
              cell: (r) => <Link href={`/my-details/requests/${r.id}`}>{t(`groups.${r.group}`)}</Link>,
            },
            { key: "person", header: t("detail.person"), sortValue: (r) => r.person.displayName, cell: (r) => <bdi>{r.person.displayName}</bdi> },
            { key: "asked", header: t("detail.asked"), sortValue: (r) => r.createdAt, cell: (r) => format.dateTime(new Date(r.createdAt), { dateStyle: "medium" }) },
            { key: "status", header: t("detail.status"), cell: (r) => <Badge tone={dataChangeTone[r.status]}>{t(`statuses.${r.status}`)}</Badge> },
          ]}
        />
      </section>
    </div>
  );
}
