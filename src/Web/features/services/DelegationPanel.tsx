"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Dialog } from "@/components/ui/Dialog";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import type { Delegation } from "@/lib/services/types";
import { isolate } from "@/lib/bidi";
import { PersonPicker } from "./PersonPicker";

type Step = "form" | "confirm" | "receipt";

/**
 * Delegation while away (spec 016 US4, FR-021, "Manage Replacement"): one colleague handles your approvals from a
 * first day to a last day. Tasks reach them marked "on behalf of" you and come back when the delegation ends.
 *
 * It is a rare, consequential task beside a list, so the page shows one compact line (who covers and when, or that
 * nothing is set) with one button, and the change runs in a guided dialog: the fields, a summary to confirm, then a
 * receipt (spec 036 DLS-115).
 */
export function DelegationPanel({ initial }: { initial: Delegation | null }) {
  const t = useTranslations("services.delegation");
  const format = useFormatter();
  const [current, setCurrent] = useState(initial);
  const [open, setOpen] = useState(false);
  const [step, setStep] = useState<Step>("form");
  const [delegateId, setDelegateId] = useState<string | null>(initial?.delegate.id ?? null);
  const [startsOn, setStartsOn] = useState(initial?.startsOn ?? "");
  const [endsOn, setEndsOn] = useState(initial?.endsOn ?? "");
  const [errors, setErrors] = useState<{ delegate?: string; startsOn?: string; endsOn?: string }>({});
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string[]>([]);
  const [ended, setEnded] = useState(false);
  const day = (iso: string) => format.dateTime(new Date(`${iso}T12:00:00`), { dateStyle: "medium" });
  const sentence = (d: Delegation) => t(d.active ? "active" : "planned", { name: isolate(d.delegate.displayName), from: day(d.startsOn), to: day(d.endsOn) });

  function start() {
    setStep("form");
    setErrors({});
    setProblem([]);
    setEnded(false);
    setDelegateId(current?.delegate.id ?? null);
    setStartsOn(current?.startsOn ?? "");
    setEndsOn(current?.endsOn ?? "");
    setOpen(true);
  }

  // Each problem is said at its own field before the summary is shown.
  function review() {
    const next: typeof errors = {};
    if (!delegateId) next.delegate = t("delegateRequired");
    if (!startsOn) next.startsOn = t("dayRequired");
    if (!endsOn) next.endsOn = t("dayRequired");
    else if (startsOn && endsOn < startsOn) next.endsOn = t("endBeforeStart");
    setErrors(next);
    setProblem([]);
    if (Object.keys(next).length === 0) setStep("confirm");
  }

  async function save() {
    setBusy(true);
    setProblem([]);
    try {
      const response = await fetch("/api/v1/me/delegation", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ delegateId, startsOn: startsOn || null, endsOn: endsOn || null }),
      });
      if (response.ok) {
        setCurrent(((await response.json()) as { delegation: Delegation }).delegation);
        setStep("receipt");
      } else {
        // The API names what it refused (a delegate who may not act, days that clash); the person fixes it in the fields.
        setProblem(await problemMessages(response));
        setStep("form");
      }
    } catch {
      setProblem([t("failed")]);
      setStep("form");
    } finally {
      setBusy(false);
    }
  }

  async function end() {
    setBusy(true);
    try {
      const response = await fetch("/api/v1/me/delegation", { method: "DELETE" });
      if (response.ok) {
        setCurrent(null);
        setEnded(true);
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="delegation-title" className="services-delegation">
      <h2 id="delegation-title" className="visually-hidden">
        {t("title")}
      </h2>
      <Alert
        title={t("title")}
        action={
          <div className="services-actions">
            <Button variant="secondary" size="sm" onClick={start}>
              {t("manage")}
            </Button>
            {current ? (
              <Button variant="quiet" size="sm" busy={busy} busyLabel={t("saving")} onClick={() => void end()}>
                {t("end")}
              </Button>
            ) : null}
          </div>
        }
      >
        <p data-testid="delegation-current" aria-live="polite">
          {current ? sentence(current) : ended ? t("ended") : t("none")}
        </p>
      </Alert>

      <Dialog
        open={open}
        onClose={() => setOpen(false)}
        title={t(step === "receipt" ? "receiptTitle" : step === "confirm" ? "confirmTitle" : "manage")}
        description={step === "form" ? t("intro") : undefined}
        footer={
          step === "form" ? (
            <>
              <Button variant="quiet" onClick={() => setOpen(false)}>
                {t("cancel")}
              </Button>
              <Button variant="primary" onClick={review}>
                {t("review")}
              </Button>
            </>
          ) : step === "confirm" ? (
            <>
              <Button variant="quiet" onClick={() => setStep("form")}>
                {t("back")}
              </Button>
              <Button variant="primary" busy={busy} busyLabel={t("saving")} onClick={() => void save()}>
                {t("save")}
              </Button>
            </>
          ) : (
            <Button variant="primary" onClick={() => setOpen(false)}>
              {t("done")}
            </Button>
          )
        }
      >
        {step === "form" ? (
          <form
            className="services-form"
            noValidate
            onSubmit={(e) => {
              e.preventDefault();
              review();
            }}
          >
            {problem.length > 0 ? (
              <div className="notice notice-error" role="alert">
                {problem.map((m) => (
                  <p key={m}>{m}</p>
                ))}
              </div>
            ) : null}
            <PersonPicker label={t("delegate")} value={delegateId} onChange={setDelegateId} error={errors.delegate} />
            <div className="services-row">
              <FormField label={t("startsOn")} name="startsOn" type="date" required value={startsOn} error={errors.startsOn} onChange={(e) => setStartsOn(e.target.value)} />
              <FormField label={t("endsOn")} name="endsOn" type="date" required min={startsOn || undefined} value={endsOn} error={errors.endsOn} onChange={(e) => setEndsOn(e.target.value)} />
            </div>
            <p className="field-hint">{t("scope")}</p>
          </form>
        ) : step === "confirm" ? (
          <div data-testid="delegation-summary">
            <PersonPicker label={t("delegate")} value={delegateId} onChange={setDelegateId} readOnly />
            <dl className="services-details">
            <dt>{t("startsOn")}</dt>
            <dd>{day(startsOn)}</dd>
            <dt>{t("endsOn")}</dt>
            <dd>{day(endsOn)}</dd>
            <dt>{t("scopeLabel")}</dt>
            <dd>{t("scope")}</dd>
            </dl>
          </div>
        ) : current ? (
          <p className="notice" role="status" data-testid="delegation-receipt">
            {sentence(current)}
          </p>
        ) : null}
      </Dialog>
    </section>
  );
}
