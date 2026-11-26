"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, FormPanel, TextAreaField } from "@/components/ui/FormField";
import { FactList } from "@/components/ui/Patterns";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { problemMessages } from "@/lib/notifications/types";
import { MILESTONE_OUTCOMES, milestoneTone, type Milestone, type MilestoneOutcome, type MilestoneReview } from "@/lib/milestones";
import "./milestones.css";

const MAX_COMMENT = 2000;

/**
 * One milestone (spec 022 US9). The reviewer, the line manager or HR when the person has none, confirms, extends with a
 * new date or ends the probation with a comment; the comment stays in TeamBeats and only the outcome goes to HR. HR
 * officers see the history and change a date they set by hand. The API checks every action again.
 */
export function MilestoneDetail({ initial }: { initial: Milestone }) {
  const t = useTranslations("milestones");
  const format = useFormatter();
  const [milestone, setMilestone] = useState(initial);
  const [outcome, setOutcome] = useState<MilestoneOutcome>("confirm");
  const [newDate, setNewDate] = useState("");
  const [comment, setComment] = useState("");
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  const day = (iso: string) => format.dateTime(new Date(`${iso}T00:00:00`), { dateStyle: "medium" });

  async function review(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setNotice(null);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/hr/milestones/${milestone.id}/review`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ outcome, newDate: outcome === "extend" ? newDate || null : null, comment }),
      });
      if (!response.ok) {
        const found = await problemMessages(response);
        setMessages(found.length > 0 ? found : [t("failed")]);
        return;
      }
      setMilestone((await response.json()) as Milestone);
      setNotice(t("review.saved"));
      setComment("");
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  const facts = [
    { key: "person", label: t("person"), value: <bdi>{milestone.person.displayName}</bdi> },
    { key: "kind", label: t("kind"), value: t(`kinds.${milestone.kind}`) },
    { key: "due", label: t("dueDate"), value: day(milestone.dueDate) },
    { key: "left", label: t("daysLeft"), value: format.number(milestone.daysLeft) },
    { key: "source", label: t("source"), value: t(`sources.${milestone.source}`) },
    { key: "status", label: t("status"), value: <Badge tone={milestoneTone[milestone.status]}>{t(`statuses.${milestone.status}`)}</Badge> },
    ...(milestone.hireDate ? [{ key: "hired", label: t("hireDate"), value: day(milestone.hireDate) }] : []),
    ...(milestone.jobTitle ? [{ key: "title", label: t("jobTitle"), value: <bdi>{milestone.jobTitle}</bdi> }] : []),
  ];

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
      <FactList facts={facts} />
      {milestone.rights.review ? (
        <FormPanel as="form" aria-labelledby="milestone-review" onSubmit={(e) => void review(e)}>
          <h2 id="milestone-review">{t("review.title")}</h2>
          <p className="field-hint">{t("review.hint")}</p>
          <RadioGroup
            legend={t("review.outcome")}
            name="milestone-outcome"
            value={outcome}
            onChange={(e) => setOutcome(e.target.value as MilestoneOutcome)}
            options={MILESTONE_OUTCOMES.map((o) => ({ value: o, label: t(`outcomes.${o}`) }))}
          />
          {outcome === "extend" ? (
            <FormField label={t("review.newDate")} name="milestone-new-date" type="date" value={newDate} onChange={(e) => setNewDate(e.target.value)} required />
          ) : null}
          <TextAreaField
            label={t("review.comment")}
            name="milestone-comment"
            value={comment}
            maxLength={MAX_COMMENT}
            required
            hint={t("review.commentHint")}
            counter={t("review.counter", { count: comment.length, max: MAX_COMMENT })}
            onChange={(e) => setComment(e.target.value)}
          />
          <div className="milestones-row">
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("saving")}>
              {t("review.submit")}
            </Button>
          </div>
        </FormPanel>
      ) : null}
      <DataTable<MilestoneReview>
        caption={t("reviews.caption")}
        rows={milestone.reviews}
        rowKey={(r) => r.id}
        density="compact"
        emptyTitle={t("reviews.empty")}
        columns={[
          { key: "outcome", header: t("review.outcome"), rowHeader: true, cell: (r) => t(`outcomes.${r.outcome}`) },
          { key: "newDate", header: t("review.newDate"), cell: (r) => (r.newDate ? day(r.newDate) : t("reviews.noDate")) },
          { key: "comment", header: t("review.comment"), cell: (r) => <bdi>{r.comment}</bdi> },
          { key: "by", header: t("reviews.by"), cell: (r) => <bdi>{r.reviewedBy.displayName}</bdi> },
          { key: "at", header: t("reviews.at"), sortValue: (r) => r.reviewedAt, cell: (r) => format.dateTime(new Date(r.reviewedAt), { dateStyle: "medium" }) },
          { key: "writeBack", header: t("reviews.writeBack"), cell: (r) => t(`writeBack.${r.writeBack}`) },
        ]}
      />
    </div>
  );
}
