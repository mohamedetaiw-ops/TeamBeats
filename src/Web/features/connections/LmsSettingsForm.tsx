"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { FormField } from "@/components/ui/FormField";
import { LMS_ASSIGN_FIELDS, LMS_AUTH, LMS_COMPLETION_FIELDS, LMS_COURSE_FIELDS, type LmsSettings, type PackConnection } from "@/lib/integrations/types";
import { ConnectionSettingsForm, type SettingsBody } from "./ConnectionSettingsForm";

const filled = (map: Record<string, string>) => Object.fromEntries(Object.entries(map).filter(([, v]) => v.trim()).map(([k, v]) => [k, v.trim()]));

/**
 * An LMS connection (spec 028 US6, through spec 017): the REST address and sign-in, where the course list is and where
 * each course field is in it, where assignments are sent and which field goes where (only mapped fields are sent),
 * and where completions since a time are read. The API checks every value.
 */
export function LmsSettingsForm({
  initial,
  submitLabel,
  busy,
  onSubmit,
}: {
  initial?: PackConnection;
  submitLabel: string;
  busy: boolean;
  onSubmit: (body: SettingsBody) => void;
}) {
  const t = useTranslations("connections.lms");
  const s = initial?.settings as Partial<LmsSettings> | undefined;
  const [coursesPath, setCoursesPath] = useState(s?.coursesPath ?? "/courses");
  const [coursesItemsPath, setCoursesItemsPath] = useState(s?.coursesItemsPath ?? "$.items");
  const [courseFields, setCourseFields] = useState<Record<string, string>>(() => Object.fromEntries(LMS_COURSE_FIELDS.map((f) => [f, s?.courseFields?.[f] ?? ""])));
  const [assignPath, setAssignPath] = useState(s?.assignPath ?? "/enrolments");
  const [assignFields, setAssignFields] = useState<Record<string, string>>(() => Object.fromEntries(LMS_ASSIGN_FIELDS.map((f) => [f, s?.assignFields?.[f] ?? ""])));
  const [completionsPath, setCompletionsPath] = useState(s?.completionsPath ?? "/completions?since={since}");
  const [completionsItemsPath, setCompletionsItemsPath] = useState(s?.completionsItemsPath ?? "$.items");
  const [completionFields, setCompletionFields] = useState<Record<string, string>>(() =>
    Object.fromEntries(LMS_COMPLETION_FIELDS.map((f) => [f, s?.completionFields?.[f] ?? ""])),
  );
  const [nextPath, setNextPath] = useState(s?.nextPath ?? "");
  const [idempotencyHeader, setIdempotencyHeader] = useState(s?.idempotencyHeader ?? "");

  const extra = {
    fields: (
      <>
        <fieldset className="connections-fieldset">
          <legend>{t("coursesTitle")}</legend>
          <FormField label={t("coursesPath")} hint={t("coursesPathHint")} name="coursesPath" dir="ltr" required value={coursesPath} onChange={(e) => setCoursesPath(e.target.value)} />
          <FormField label={t("itemsPath")} hint={t("pathHint")} name="coursesItemsPath" dir="ltr" required value={coursesItemsPath} onChange={(e) => setCoursesItemsPath(e.target.value)} />
          {LMS_COURSE_FIELDS.map((f) => (
            <FormField
              key={f}
              label={t(`courseFields.${f}`)}
              hint={t("itemPathHint")}
              name={`course-${f}`}
              dir="ltr"
              required={f !== "summary" && f !== "language"}
              value={courseFields[f] ?? ""}
              onChange={(e) => setCourseFields((m) => ({ ...m, [f]: e.target.value }))}
            />
          ))}
          <FormField label={t("nextPath")} hint={t("nextPathHint")} name="nextPath" dir="ltr" value={nextPath} onChange={(e) => setNextPath(e.target.value)} />
        </fieldset>
        <fieldset className="connections-fieldset">
          <legend>{t("assignTitle")}</legend>
          <FormField label={t("assignPath")} hint={t("assignPathHint")} name="assignPath" dir="ltr" required value={assignPath} onChange={(e) => setAssignPath(e.target.value)} />
          <p className="field-hint">{t("assignFieldsHint")}</p>
          {LMS_ASSIGN_FIELDS.map((f) => (
            <FormField
              key={f}
              label={t(`assignFields.${f}`)}
              name={`assign-${f}`}
              dir="ltr"
              required={f === "course_id" || f === "learner_email"}
              value={assignFields[f] ?? ""}
              onChange={(e) => setAssignFields((m) => ({ ...m, [f]: e.target.value }))}
            />
          ))}
          <FormField label={t("idempotencyHeader")} hint={t("idempotencyHeaderHint")} name="idempotencyHeader" dir="ltr" value={idempotencyHeader} onChange={(e) => setIdempotencyHeader(e.target.value)} />
        </fieldset>
        <fieldset className="connections-fieldset">
          <legend>{t("completionsTitle")}</legend>
          <FormField label={t("completionsPath")} hint={t("completionsPathHint")} name="completionsPath" dir="ltr" required value={completionsPath} onChange={(e) => setCompletionsPath(e.target.value)} />
          <FormField label={t("itemsPath")} hint={t("pathHint")} name="completionsItemsPath" dir="ltr" required value={completionsItemsPath} onChange={(e) => setCompletionsItemsPath(e.target.value)} />
          {LMS_COMPLETION_FIELDS.map((f) => (
            <FormField
              key={f}
              label={t(`completionFields.${f}`)}
              hint={t("itemPathHint")}
              name={`completion-${f}`}
              dir="ltr"
              required={f !== "score"}
              value={completionFields[f] ?? ""}
              onChange={(e) => setCompletionFields((m) => ({ ...m, [f]: e.target.value }))}
            />
          ))}
        </fieldset>
      </>
    ),
    values: (): Partial<LmsSettings> => ({
      coursesPath: coursesPath.trim(),
      coursesItemsPath: coursesItemsPath.trim(),
      courseFields: filled(courseFields),
      assignPath: assignPath.trim(),
      assignFields: filled(assignFields),
      completionsPath: completionsPath.trim(),
      completionsItemsPath: completionsItemsPath.trim(),
      completionFields: filled(completionFields),
      nextPath: nextPath.trim() || null,
      idempotencyHeader: idempotencyHeader.trim() || null,
    }),
  };

  return <ConnectionSettingsForm initial={initial} submitLabel={submitLabel} busy={busy} onSubmit={onSubmit} authTypes={LMS_AUTH} extra={extra} formLabel={t("formLabel")} />;
}
