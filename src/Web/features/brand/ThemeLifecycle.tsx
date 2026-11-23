"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import type { BrandApprovalState, BrandDraft, BrandRelease, BrandScheduleState } from "@/lib/brand/types";
import { problemMessages } from "@/lib/notifications/types";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField } from "@/components/ui/FormField";

type Result = { ok: boolean; messages: string[] } | null;

const hows = ["direct", "approved", "scheduled", "reverted"] as const;

/**
 * Publishing a brand (spec 033 R2 T014, US2, FR-012): the draft kept beside the version in force, publishing it now, at a
 * tenant-local time or through approval, and every earlier version with how it went live, ready to bring back. Whether the
 * person may publish comes from the API's approval state; the API checks every action again.
 */
export function ThemeLifecycle({
  brandId,
  draft,
  releases,
  schedule,
  approval,
}: {
  brandId: string;
  draft: BrandDraft;
  releases: BrandRelease[];
  schedule: BrandScheduleState | null;
  approval: BrandApprovalState | null;
}) {
  const t = useTranslations("brand.lifecycle");
  const locale = useLocale();
  const router = useRouter();
  const titleId = useId();
  const [reason, setReason] = useState("");
  const [goLive, setGoLive] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);
  const base = `/api/v1/experience/brands/${brandId}`;
  const publish = approval?.publish ?? false;
  const pending = approval?.pending ?? null;
  const last = approval?.last ?? null;
  const waiting = schedule?.current?.status === "scheduled" ? schedule.current : null;
  const zone = schedule?.timeZone ?? "UTC";
  const when = (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short", timeZone: zone }).format(new Date(iso));

  async function send(path: string, method: string, body: unknown, done: string) {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(`${base}${path}`, {
        method,
        headers: body === null ? undefined : { "content-type": "application/json" },
        body: body === null ? undefined : JSON.stringify(body),
      });
      if (response.ok) {
        setResult({ ok: true, messages: [done] });
        setReason("");
        setGoLive("");
        router.refresh();
        return;
      }
      const messages = await problemMessages(response);
      setResult({ ok: false, messages: messages.length > 0 ? messages : [t("failed")] });
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  const reasonValue = reason.trim() || null;

  return (
    <section className="brand-section brand-lifecycle" aria-labelledby={titleId} data-testid="theme-lifecycle">
      <h2 id={titleId}>{t("title")}</h2>
      <p className="notice" data-testid="theme-draft-state">
        {draft.hasDraft
          ? t("draftKept", { version: String(draft.baseVersion), when: draft.updatedAt ? when(draft.updatedAt) : "" })
          : t("noDraft", { version: String(draft.currentVersion) })}
      </p>
      {draft.hasDraft && draft.stale ? (
        <Alert tone="warning" title={t("staleTitle")}>
          {t("stale", { version: String(draft.currentVersion) })}
        </Alert>
      ) : null}

      {pending ? (
        <div className="brand-lifecycle-block" data-testid="theme-pending">
          <p className="notice" role="status">
            {t("pending", { when: when(pending.submittedAt), count: pending.changes.length + pending.assetChanges.length })}
          </p>
          <Button variant="secondary" busy={busy} busyLabel={t("working")} onClick={() => void send("/withdraw", "POST", null, t("withdrawn"))}>
            {t("withdraw")}
          </Button>
        </div>
      ) : last ? (
        <p className="notice" data-testid="theme-last">
          {last.status === "approved"
            ? t("lastApproved", { version: String(last.version ?? "") })
            : last.status === "returned"
              ? t("lastReturned", { comment: last.comment ?? "" })
              : t("lastWithdrawn")}
        </p>
      ) : null}

      {draft.hasDraft && !pending ? (
        <div className="brand-lifecycle-block">
          <FormField label={t("reason")} name="theme-reason" value={reason} maxLength={500} hint={t("reasonHint")} onChange={(e) => setReason(e.target.value)} />
          <div className="button-row">
            {publish ? (
              <Button variant="primary" busy={busy} busyLabel={t("working")} onClick={() => void send("/draft/publish", "POST", { reason: reasonValue }, t("published"))}>
                {t("publish")}
              </Button>
            ) : (
              <Button variant="primary" busy={busy} busyLabel={t("working")} onClick={() => void send("/submit", "POST", { reason: reasonValue }, t("submitted"))}>
                {t("submit")}
              </Button>
            )}
            <ConfirmAction
              label={t("discard")}
              question={t("discardQuestion")}
              confirmLabel={t("discardConfirm")}
              cancelLabel={t("cancel")}
              onConfirm={() => send("/draft", "DELETE", null, t("discarded"))}
            />
          </div>
        </div>
      ) : null}

      {publish ? (
        <div className="brand-lifecycle-block" data-testid="theme-schedule">
          <h3>{t("scheduleTitle")}</h3>
          {waiting ? (
            <>
              <p className="notice" role="status" data-testid="theme-schedule-waiting">
                {t("scheduledFor", { when: when(waiting.goLiveAt) })}
              </p>
              <Button variant="secondary" busy={busy} busyLabel={t("working")} onClick={() => void send("/schedule", "DELETE", null, t("scheduleCancelled"))}>
                {t("cancelSchedule")}
              </Button>
            </>
          ) : (
            <>
              {schedule?.current?.status === "failed" ? (
                <Alert tone="warning" title={t("scheduleFailedTitle")}>
                  {t("scheduleFailed", { when: when(schedule.current.goLiveAt) })}
                </Alert>
              ) : null}
              <FormField
                label={t("goLive")}
                name="theme-go-live"
                type="datetime-local"
                hint={t("zone", { zone })}
                value={goLive}
                onChange={(e) => setGoLive(e.target.value)}
              />
              <Button
                variant="secondary"
                busy={busy}
                busyLabel={t("working")}
                disabled={!draft.hasDraft || goLive === "" || pending !== null}
                onClick={() => void send("/schedule", "PUT", { goLiveAt: goLive, reason: reasonValue }, t("scheduled"))}
              >
                {t("schedule")}
              </Button>
            </>
          )}
        </div>
      ) : null}

      <div aria-live="polite">
        {result ? (
          <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "field-error"} data-testid="theme-result">
            {result.messages.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
      </div>

      <h3>{t("versions")}</h3>
      <ol className="brand-releases" data-testid="theme-releases">
        {releases.map((r) => (
          <li key={r.version} className="brand-release">
            <div className="brand-release-head">
              <span>{t("versionLine", { version: String(r.version), when: when(r.publishedAt) })}</span>
              {r.current ? <Badge tone="success">{t("inForce")}</Badge> : null}
              {r.how && hows.includes(r.how) ? <Badge>{t(`how.${r.how}`)}</Badge> : null}
            </div>
            {r.rollbackOf ? <p className="field-hint">{t("restored", { version: String(r.rollbackOf) })}</p> : null}
            {r.reason ? (
              <p className="field-hint">
                <bdi>{r.reason}</bdi>
              </p>
            ) : null}
            {publish && !r.current ? (
              <ConfirmAction
                label={t("revertLabel", { version: String(r.version) })}
                question={t("revertQuestion", { version: String(r.version) })}
                confirmLabel={t("revertConfirm")}
                cancelLabel={t("cancel")}
                onConfirm={() => send("/revert", "POST", { version: r.version, reason: reasonValue }, t("reverted", { version: String(r.version) }))}
              />
            ) : null}
          </li>
        ))}
      </ol>
    </section>
  );
}
