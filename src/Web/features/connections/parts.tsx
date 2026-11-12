"use client";

import { useTranslations } from "next-intl";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Icon } from "@/components/ui/Icon";
import { typeLook, WIZARD_STEPS, type WizardStep } from "@/lib/integrations/catalogue";
import {
  CHECK_RESULTS,
  CONNECTION_STATUSES,
  ERROR_CLASSES,
  M365_TEST_RESULTS,
  checkResult,
  known,
  type FeatureTestResult,
  type PackCheck,
} from "@/lib/integrations/types";

const STATUS_TONES: Record<(typeof CONNECTION_STATUSES)[number], BadgeTone> = {
  healthy: "success",
  degraded: "danger",
  needs_consent: "warning",
  credential_expiring: "warning",
  disabled: "neutral",
};

/** A connection status as words, never by color alone; the tone repeats what the words say. */
export function StatusBadge({ status }: { status: string }) {
  const t = useTranslations("connections");
  return known(CONNECTION_STATUSES, status) ? <Badge tone={STATUS_TONES[status]}>{t(`status.${status}`)}</Badge> : <Badge>{status}</Badge>;
}

/**
 * The mark of the product a connection reaches (spec 036 DLS-075): the owner's published mark, unaltered, from
 * `public/connectors`, or an icon from the icon set for types without one. Always beside the type's name, so it is
 * decorative.
 */
export function ProductMark({ kind, size = "md" }: { kind: string; size?: "sm" | "md" | "lg" }) {
  const look = typeLook(kind);
  const px = size === "sm" ? 20 : size === "lg" ? 32 : 26;
  return (
    <span className="connections-mark" data-size={size} data-mark={look.mark ? "product" : "icon"}>
      {look.mark ? (
        // A small local file of fixed size; the Next image optimizer adds nothing here.
        // eslint-disable-next-line @next/next/no-img-element
        <img src={look.mark} alt="" width={px} height={px} decoding="async" />
      ) : (
        <Icon name={look.icon} size={px - 4} />
      )}
    </span>
  );
}

/** Where the administrator is in the "Add connection" wizard (spec 006 FR-016, spec 036 DLS-076). */
export function WizardSteps({ current }: { current: WizardStep }) {
  const t = useTranslations("connections");
  const at = WIZARD_STEPS.indexOf(current);
  return (
    <ol className="connections-steps" aria-label={t("wizard.progress")}>
      {WIZARD_STEPS.map((step, i) => {
        const state = i < at ? "done" : i === at ? "current" : "todo";
        return (
          <li key={step} data-state={state} aria-current={state === "current" ? "step" : undefined}>
            <span className="connections-step-mark" aria-hidden="true">
              {state === "done" ? <Icon name="check" size={14} /> : i + 1}
            </span>
            <span className="connections-step-label">
              {t(`wizard.steps.${step}`)}
              {state === "todo" ? null : <span className="visually-hidden">{t(`wizard.stepState.${state}`)}</span>}
            </span>
          </li>
        );
      })}
    </ol>
  );
}

/** What went wrong, from the runtime's error class (spec 017 section 6). */
export function ErrorText({ errorClass }: { errorClass: string | null | undefined }) {
  const t = useTranslations("connections");
  if (!errorClass) return null;
  return <>{known(ERROR_CLASSES, errorClass) ? t(`errors.${errorClass}`) : errorClass}</>;
}

/** Each check of a connection test, passed or failed in words. */
export function Checks({ checks }: { checks: PackCheck[] }) {
  const t = useTranslations("connections");
  return (
    <ul className="connections-checks">
      {checks.map((c) => {
        const result = checkResult(c.resultKey);
        return (
          <li key={c.name} data-ok={c.ok}>
            <span className="connections-check-mark">{t(c.ok ? "checks.passed" : "checks.failedMark")}</span> {known(CHECK_RESULTS, result) ? t(`checks.${result}`) : result}
            {c.errorClass ? (
              <>
                {" "}
                (<ErrorText errorClass={c.errorClass} />)
              </>
            ) : null}
          </li>
        );
      })}
    </ul>
  );
}

/** The test result of one Microsoft 365 feature. */
export function TestLine({ result }: { result: FeatureTestResult }) {
  const t = useTranslations("connections");
  const key = checkResult(result.resultKey);
  return (
    <p data-ok={result.ok}>
      <span className="connections-check-mark">{t(result.ok ? "checks.passed" : "checks.failedMark")}</span> {known(M365_TEST_RESULTS, key) ? t(`m365.tests.${key}`) : key}
      {result.users != null ? <> {t("m365.counts", { users: result.users, groups: result.groups ?? 0 })}</> : null}
      {result.missing.length > 0 ? (
        <>
          {" "}
          {t("m365.missing")} <bdi dir="ltr">{result.missing.join(", ")}</bdi>
        </>
      ) : null}
      {result.broader.length > 0 ? (
        <>
          {" "}
          {t("m365.broader")} <bdi dir="ltr">{result.broader.join(", ")}</bdi>
        </>
      ) : null}
    </p>
  );
}

export function Messages({ messages, ok = false }: { messages: string[]; ok?: boolean }) {
  if (messages.length === 0) return null;
  return (
    <div role={ok ? "status" : "alert"} className={ok ? "notice" : "notice notice-error"}>
      {messages.map((m) => (
        <p key={m}>{m}</p>
      ))}
    </div>
  );
}
