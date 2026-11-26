"use client";

import Link from "next/link";
import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, FormPanel } from "@/components/ui/FormField";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { PersonPicker } from "@/features/services/PersonPicker";
import { problemMessages } from "@/lib/notifications/types";
import { MILESTONE_KINDS, milestoneTone, type Milestone, type MilestoneKind } from "@/lib/milestones";
import "./milestones.css";

/**
 * The milestones of the people an HR officer covers (spec 022 US9): probation and contract end dates from HR or set by
 * hand, nearest first. A date by hand is for people HR has no date for; the API refuses one HR already holds.
 */
export function MilestoneList({ initial }: { initial: Milestone[] }) {
  const t = useTranslations("milestones");
  const format = useFormatter();
  const [rows, setRows] = useState(initial);
  const [person, setPerson] = useState<string | null>(null);
  const [kind, setKind] = useState<MilestoneKind>("probation");
  const [date, setDate] = useState("");
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (!person) {
      setMessages([t("set.personRequired")]);
      return;
    }
    setBusy(true);
    setNotice(null);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/hr/milestones/people/${person}/${kind}`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ dueDate: date || null }),
      });
      if (!response.ok) {
        const found = await problemMessages(response);
        setMessages(found.length > 0 ? found : [t("failed")]);
        return;
      }
      const list = await fetch("/api/v1/hr/milestones", { cache: "no-store" });
      if (list.ok) setRows((await list.json()) as Milestone[]);
      setNotice(date ? t("set.saved") : t("set.cleared"));
      setDate("");
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="milestones">
      {notice ? (
        <p className="notice" role="status">
          {notice}
        </p>
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <DataTable<Milestone>
        caption={t("list.caption")}
        rows={rows}
        rowKey={(r) => r.id}
        density="compact"
        emptyTitle={t("list.empty")}
        columns={[
          {
            key: "person",
            header: t("person"),
            rowHeader: true,
            sortValue: (r) => r.person.displayName,
            cell: (r) => (
              <Link href={`/hr/milestones/${r.id}`}>
                <bdi>{r.person.displayName}</bdi>
              </Link>
            ),
          },
          { key: "kind", header: t("kind"), cell: (r) => t(`kinds.${r.kind}`) },
          { key: "due", header: t("dueDate"), sortValue: (r) => r.dueDate, cell: (r) => format.dateTime(new Date(`${r.dueDate}T00:00:00`), { dateStyle: "medium" }) },
          { key: "left", header: t("daysLeft"), sortValue: (r) => r.daysLeft, cell: (r) => format.number(r.daysLeft) },
          { key: "source", header: t("source"), cell: (r) => t(`sources.${r.source}`) },
          { key: "status", header: t("status"), cell: (r) => <Badge tone={milestoneTone[r.status]}>{t(`statuses.${r.status}`)}</Badge> },
        ]}
      />
      <FormPanel as="form" aria-labelledby="milestone-set" onSubmit={(e) => void save(e)}>
        <h2 id="milestone-set">{t("set.title")}</h2>
        <p className="field-hint">{t("set.hint")}</p>
        <PersonPicker label={t("person")} value={person} onChange={setPerson} />
        <RadioGroup
          legend={t("kind")}
          name="milestone-kind"
          value={kind}
          onChange={(e) => setKind(e.target.value as MilestoneKind)}
          options={MILESTONE_KINDS.map((k) => ({ value: k, label: t(`kinds.${k}`) }))}
        />
        <div className="milestones-row">
          <FormField label={t("dueDate")} name="milestone-date" type="date" value={date} onChange={(e) => setDate(e.target.value)} hint={t("set.dateHint")} />
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("saving")}>
            {t("set.save")}
          </Button>
        </div>
      </FormPanel>
    </div>
  );
}
