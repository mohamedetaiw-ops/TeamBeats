"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import type { BrandApprovalState, BrandDraft, BrandImpact, BrandRelease, BrandScheduleState } from "@/lib/brand/types";
import { ImpactDialog } from "@/features/builder/editor/ImpactDialog";
import { BrandPreview, type PreviewSample } from "./BrandPreview";
import { problemMessages } from "@/lib/notifications/types";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField } from "@/components/ui/FormField";

type Result = { ok: boolean; messages: string[] } | null;

const hows = ["direct", "approved", "scheduled", "reverted"] as const;

/** The design review checklist of docs/design-system/testing.md, as it applies to a brand (spec 036 DLS-023). */
const reviewItems = ["contrast", "modes", "directions", "widths", "designs", "assets", "scopes"] as const;

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
  samples,
}: {
  brandId: string;
  samples: { ltr: PreviewSample; rtl: PreviewSample };
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
  const [impact, setImpact] = useState<BrandImpact | null>(null);
  const tTokens = useTranslations("brand");
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
  const named = (names: Record<string, string>) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";
  const tokenName = (token: string) => (tTokens.has(`tokenNames.${token}`) ? tTokens(`tokenNames.${token}`) : token);

  /** Shows what the draft reaches before it goes live; without impact reports in the package it publishes in one step. */
  async function review() {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(`${base}/draft/impact`, { cache: "no-store" });
      if (response.ok) {
        setImpact((await response.json()) as BrandImpact);
        return;
      }
      if (response.status !== 403) {
        const messages = await problemMessages(response);
        setResult({ ok: false, messages: messages.length > 0 ? messages : [t("failed")] });
        return;
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
      return;
    } finally {
      setBusy(false);
    }
    await send("/draft/publish", "POST", { reason: reasonValue }, t("published"));
  }

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

      {draft.hasDraft ? (
        <div className="brand-lifecycle-block" data-testid="theme-review">
          <h3>{t("review.title")}</h3>
          <p className="notice">{t("review.intro")}</p>
          <ul className="brand-review">
            {reviewItems.map((key) => (
              <li key={key}>{t(`review.items.${key}`)}</li>
            ))}
          </ul>
        </div>
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
              <Button variant="primary" busy={busy} busyLabel={t("working")} onClick={() => void review()}>
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

      {impact ? (
        <ImpactDialog
          report={impact.report}
          submit={false}
          busy={busy}
          onCancel={() => setImpact(null)}
          onConfirm={() => void send("/draft/publish", "POST", { reason: reasonValue }, t("published")).then(() => setImpact(null))}
        >
          <div className="brand-impact" data-testid="theme-impact">
            <p>
              {t("impactChanges", { count: impact.changes.length })}{" "}
              {impact.changes.map((c, i) => (
                <span key={c}>
                  {i > 0 ? t("separator") : null}
                  <bdi>{tokenName(c)}</bdi>
                </span>
              ))}
            </p>
            {impact.excluded.length > 0 ? (
              <p data-testid="theme-impact-excluded">{t("impactExcluded", { units: impact.excluded.map((u) => named(u.names)).join(t("separator")) })}</p>
            ) : null}
            {impact.overridden.length > 0 ? (
              <ul className="brand-impact-list" data-testid="theme-impact-overridden">
                {impact.overridden.map((o) => (
                  <li key={`${o.scope}:${o.token}`}>
                    {t("impactOverridden", { scope: o.scope === "tenant" ? t("tenant") : named(o.scopeNames), token: tokenName(o.token) })}
                  </li>
                ))}
              </ul>
            ) : null}
            <BrandPreview
              headingId={`${titleId}-impact-preview`}
              title={t("impactPreview", { scope: impact.previewScope === "tenant" ? t("tenant") : named(impact.previewNames) })}
              preview={impact.preview}
              logoUrl={null}
              logoAlt=""
              panes={[
                { mode: "light", dir: "ltr", lang: "en", sample: samples.ltr, label: t("impactPane", { direction: tTokens("preview.ltr") }) },
                { mode: "light", dir: "rtl", lang: "ar", sample: samples.rtl, label: t("impactPane", { direction: tTokens("preview.rtl") }) },
              ]}
            />
          </div>
        </ImpactDialog>
      ) : null}

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
