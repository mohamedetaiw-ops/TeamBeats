"use client";

import Link from "next/link";
import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField } from "@/components/ui/FormField";
import { courseHref } from "@/lib/learning";
import { problemMessages } from "@/lib/notifications/types";
import type { PolicyView } from "@/lib/policies/types";
import "./policies.css";

const guidIn = (text: string) => text.match(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i)?.[0] ?? null;

/**
 * Linked training on a policy page (spec 028 US4 scenario 2): a compliance officer links a published course, by its link
 * or ID, and chooses whether attesting waits for its completion. The next material version in force gives the course to
 * everyone the policy applies to. The API checks who may link and that the course is published.
 */
export function TrainingLink({ policy }: { policy: PolicyView }) {
  const t = useTranslations("policies.training");
  const ids = useId();
  const [training, setTraining] = useState(policy.training ?? null);
  const [course, setCourse] = useState(policy.training?.courseId ?? "");
  const [first, setFirst] = useState(policy.training?.beforeAttestation ?? true);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  async function save(courseId: string | null) {
    setBusy(true);
    setMessages([]);
    setSaved(false);
    try {
      const response = await fetch(`/api/v1/policies/${encodeURIComponent(policy.id)}/training`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ courseId, beforeAttestation: first }),
      });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      const next = (await response.json()) as PolicyView;
      setTraining(next.training ?? null);
      setCourse(next.training?.courseId ?? "");
      setSaved(true);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="policies-card" aria-labelledby={`${ids}-title`}>
      <h2 id={`${ids}-title`}>{t("title")}</h2>
      <p className="policies-meta">{t("hint")}</p>
      {training ? (
        <p>
          <Link href={courseHref(training.courseId)}>{t("linked")}</Link> {training.beforeAttestation ? t("waits") : t("doesNotWait")}
        </p>
      ) : (
        <p className="policies-meta">{t("none")}</p>
      )}
      <FormField label={t("course")} hint={t("courseHint")} name="policy-training-course" value={course} onChange={(e) => setCourse(e.target.value)} />
      <Checkbox name="policy-training-first" label={t("beforeAttestation")} checked={first} onChange={(e) => setFirst(e.target.checked)} />
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {saved ? (
        <p className="policies-meta" role="status">
          {t("saved")}
        </p>
      ) : null}
      <div className="button-row">
        <Button variant="primary" busy={busy} busyLabel={t("saving")} disabled={!guidIn(course)} onClick={() => void save(guidIn(course))}>
          {t("save")}
        </Button>
        {training ? (
          <Button busy={busy} busyLabel={t("saving")} onClick={() => void save(null)}>
            {t("unlink")}
          </Button>
        ) : null}
      </div>
    </section>
  );
}
