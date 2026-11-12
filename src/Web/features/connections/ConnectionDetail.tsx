"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { TICKETING_KINDS, isTicketing, type PackCheck, type PackConnection, type PackTestResult, type WebhookEventChoice } from "@/lib/integrations/types";
import { ClockSettingsForm, type ClockBody } from "./ClockSettingsForm";
import { ConnectionSettingsForm, type SettingsBody } from "./ConnectionSettingsForm";
import { FinanceSettingsForm } from "./FinanceSettingsForm";
import { LmsSettingsForm } from "./LmsSettingsForm";
import { HrRestSettingsForm, HrSftpSettingsForm, type HrSftpBody } from "./HrSettingsForm";
import { GoogleSettingsForm, type GoogleBody } from "./GoogleSettingsForm";
import { OnPremSettingsForm, type OnPremBody } from "./OnPremSettingsForm";
import { SlackSettingsForm, type SlackBody } from "./SlackSettingsForm";
import { WebhookSettingsForm, type WebhookBody } from "./WebhookSettingsForm";
import { Checks, Messages, StatusBadge } from "./parts";
import { send } from "./request";

/**
 * One connection (spec 017 US1): status, the last test with each check, test, enable, disable, change settings and
 * credentials, and delete with confirmation. A connection is enabled only after the administrator chooses to.
 */
export function ConnectionDetail({ initial, webhookEvents = [] }: { initial: PackConnection; webhookEvents?: WebhookEventChoice[] }) {
  const t = useTranslations("connections");
  const format = useFormatter();
  const router = useRouter();
  const returnTo = `/admin/connections/${initial.id}`;
  const [view, setView] = useState(initial);
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<{ ok: boolean; list: string[] }>({ ok: true, list: [] });
  const [checks, setChecks] = useState<PackCheck[]>(initial.lastTest?.checks ?? []);
  const base = `/api/v1/integrations/pack-connections/${initial.id}`;

  async function run<T>(key: string, method: string, path: string, body: unknown, done: (data: T) => string | null) {
    setBusy(key);
    setMessages({ ok: true, list: [] });
    const result = await send<T>(method, path, body, returnTo, t("detail.failed"));
    setBusy(null);
    if (result.ok) {
      const note = done(result.data);
      setMessages({ ok: true, list: note ? [note] : [] });
    } else {
      setMessages({ ok: false, list: result.messages });
    }
  }

  const date = (iso: string | null) => (iso ? format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" }) : t("detail.never"));

  return (
    <div className="connections">
      <section className="connections-section" aria-labelledby="state-heading">
        <h2 id="state-heading">{t("detail.state")}</h2>
        <dl className="connections-facts">
          <dt>{t("detail.status")}</dt>
          <dd>
            <StatusBadge status={view.status} />
          </dd>
          <dt>{t("detail.lastSuccess")}</dt>
          <dd>{date(view.lastSuccessAt)}</dd>
          <dt>{t("detail.lastChecked")}</dt>
          <dd>{date(view.lastCheckedAt)}</dd>
          {view.credentialExpiresOn ? (
            <>
              <dt>{t("detail.credentialExpires")}</dt>
              <dd>{format.dateTime(new Date(`${view.credentialExpiresOn}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" })}</dd>
            </>
          ) : null}
          {view.circuitOpen ? (
            <>
              <dt>{t("detail.circuit")}</dt>
              <dd>{t("detail.circuitOpen")}</dd>
            </>
          ) : null}
        </dl>
        {checks.length > 0 ? (
          <>
            <h3>{t("detail.checks")}</h3>
            <Checks checks={checks} />
          </>
        ) : null}
        <div className="connections-row">
          <Button
            busy={busy === "test"}
            busyLabel={t("detail.working")}
            onClick={() =>
              void run<PackTestResult>("test", "POST", `${base}:test`, undefined, (r) => {
                setChecks(r.checks);
                return t(r.status === "ok" ? "detail.testPassed" : "detail.testFailed");
              })
            }
          >
            {t("detail.test")}
          </Button>
          {view.enabled ? (
            <Button busy={busy === "toggle"} busyLabel={t("detail.working")} onClick={() => void run<PackConnection>("toggle", "POST", `${base}:disable`, undefined, (v) => (setView(v), t("detail.disabled")))}>
              {t("detail.disable")}
            </Button>
          ) : (
            <Button variant="primary" busy={busy === "toggle"} busyLabel={t("detail.working")} onClick={() => void run<PackConnection>("toggle", "POST", `${base}:enable`, undefined, (v) => (setView(v), t("detail.enabled")))}>
              {t("detail.enable")}
            </Button>
          )}
        </div>
        <Messages messages={messages.list} ok={messages.ok} />
      </section>

      {view.kind === "custom_rest" ? (
        <section className="connections-section" aria-labelledby="settings-heading">
          <h2 id="settings-heading">{t("detail.settings")}</h2>
          <ConnectionSettingsForm
            initial={view}
            submitLabel={t("detail.save")}
            busy={busy === "save"}
            onSubmit={(body: SettingsBody) => void run<PackConnection>("save", "PATCH", base, body, (v) => (setView(v), t("detail.saved")))}
          />
        </section>
      ) : null}

      {isTicketing(view.kind) ? (
        <section className="connections-section" aria-labelledby="settings-heading">
          <h2 id="settings-heading">{t("detail.settings")}</h2>
          <ConnectionSettingsForm
            key={JSON.stringify(view.settings)}
            initial={view}
            authTypes={TICKETING_KINDS[view.kind].auth}
            desk={TICKETING_KINDS[view.kind].desk}
            submitLabel={t("detail.save")}
            busy={busy === "save"}
            onSubmit={(body: SettingsBody) => void run<PackConnection>("save", "PATCH", base, body, (v) => (setView(v), t("detail.saved")))}
          />
        </section>
      ) : null}

      {view.kind === "slack" ? (
        <section className="connections-section" aria-labelledby="settings-heading">
          <h2 id="settings-heading">{t("detail.settings")}</h2>
          <SlackSettingsForm
            key={JSON.stringify(view.settings)}
            initial={view}
            submitLabel={t("detail.save")}
            busy={busy === "save"}
            onSubmit={(body: SlackBody) => void run<PackConnection>("save", "PATCH", base, body, (v) => (setView(v), t("detail.saved")))}
          />
        </section>
      ) : null}

      {view.kind === "clock_import" ? (
        <section className="connections-section" aria-labelledby="settings-heading">
          <h2 id="settings-heading">{t("detail.settings")}</h2>
          <ClockSettingsForm
            key={JSON.stringify(view.settings)}
            initial={view}
            submitLabel={t("detail.save")}
            busy={busy === "save"}
            onSubmit={(body: ClockBody) => void run<PackConnection>("save", "PATCH", base, body, (v) => (setView(v), t("detail.saved")))}
          />
        </section>
      ) : null}

      {view.kind === "google_workspace" ? (
        <section className="connections-section" aria-labelledby="settings-heading">
          <h2 id="settings-heading">{t("detail.settings")}</h2>
          <GoogleSettingsForm
            key={JSON.stringify(view.settings)}
            initial={view}
            submitLabel={t("detail.save")}
            busy={busy === "save"}
            onSubmit={(body: GoogleBody) => void run<PackConnection>("save", "PATCH", base, body, (v) => (setView(v), t("detail.saved")))}
          />
          {(view.settings as { features?: string[] } | null)?.features?.includes("directory") ? (
            <>
              <h3>{t("google.directoryTitle")}</h3>
              <p>{t("google.directoryIntro")}</p>
              <div className="connections-row">
                <Button
                  busy={busy === "directory"}
                  busyLabel={t("detail.working")}
                  onClick={() => void run<unknown>("directory", "PUT", "/api/v1/tenant/directory", { connectionId: view.id }, () => t("google.directoryUsed"))}
                >
                  {t("google.useForDirectory")}
                </Button>
              </div>
            </>
          ) : null}
        </section>
      ) : null}

      {view.kind === "finance_rest" ? (
        <section className="connections-section" aria-labelledby="settings-heading">
          <h2 id="settings-heading">{t("detail.settings")}</h2>
          <FinanceSettingsForm
            key={JSON.stringify(view.settings)}
            initial={view}
            submitLabel={t("detail.save")}
            busy={busy === "save"}
            onSubmit={(body: SettingsBody) => void run<PackConnection>("save", "PATCH", base, body, (v) => (setView(v), t("detail.saved")))}
          />
        </section>
      ) : null}

      {view.kind === "lms_rest" ? (
        <section className="connections-section" aria-labelledby="settings-heading">
          <h2 id="settings-heading">{t("detail.settings")}</h2>
          <LmsSettingsForm
            key={JSON.stringify(view.settings)}
            initial={view}
            submitLabel={t("detail.save")}
            busy={busy === "save"}
            onSubmit={(body: SettingsBody) => void run<PackConnection>("save", "PATCH", base, body, (v) => (setView(v), t("detail.saved")))}
          />
        </section>
      ) : null}

      {view.kind === "hr_rest" ? (
        <section className="connections-section" aria-labelledby="settings-heading">
          <h2 id="settings-heading">{t("detail.settings")}</h2>
          <HrRestSettingsForm
            key={JSON.stringify(view.settings)}
            initial={view}
            submitLabel={t("detail.save")}
            busy={busy === "save"}
            onSubmit={(body: SettingsBody) => void run<PackConnection>("save", "PATCH", base, body, (v) => (setView(v), t("detail.saved")))}
          />
          <p>
            <Link href="/admin/hr-integration">{t("hr.openIntegration")}</Link>
          </p>
        </section>
      ) : null}

      {view.kind === "hr_sftp" ? (
        <section className="connections-section" aria-labelledby="settings-heading">
          <h2 id="settings-heading">{t("detail.settings")}</h2>
          <HrSftpSettingsForm
            key={JSON.stringify(view.settings)}
            initial={view}
            submitLabel={t("detail.save")}
            busy={busy === "save"}
            onSubmit={(body: HrSftpBody) => void run<PackConnection>("save", "PATCH", base, body, (v) => (setView(v), t("detail.saved")))}
          />
          <p>
            <Link href="/admin/hr-integration">{t("hr.openIntegration")}</Link>
          </p>
        </section>
      ) : null}

      {view.kind === "webhook" ? (
        <section className="connections-section" aria-labelledby="settings-heading">
          <h2 id="settings-heading">{t("detail.settings")}</h2>
          <WebhookSettingsForm
            key={JSON.stringify(view.settings)}
            initial={view}
            events={webhookEvents}
            submitLabel={t("detail.save")}
            busy={busy === "save"}
            onSubmit={(body: WebhookBody) => void run<PackConnection>("save", "PATCH", base, body, (v) => (setView(v), t("detail.saved")))}
          />
        </section>
      ) : null}

      {view.kind === "onprem_ad" ? (
        <section className="connections-section" aria-labelledby="settings-heading">
          <h2 id="settings-heading">{t("detail.settings")}</h2>
          <OnPremSettingsForm
            key={JSON.stringify(view.settings)}
            initial={view}
            submitLabel={t("detail.save")}
            busy={busy === "save"}
            onSubmit={(body: OnPremBody) => void run<PackConnection>("save", "PATCH", base, body, (v) => (setView(v), t("detail.saved")))}
          />
        </section>
      ) : null}

      <section className="connections-section" aria-labelledby="delete-heading">
        <h2 id="delete-heading">{t("detail.deleteTitle")}</h2>
        <p>{t("detail.deleteIntro")}</p>
        <ConfirmAction
          label={t("detail.delete")}
          question={t("detail.deleteQuestion", { name: view.displayName, count: view.actions.length })}
          confirmLabel={t("detail.deleteConfirm")}
          cancelLabel={t("detail.cancel")}
          onConfirm={async () => {
            const result = await send<null>("DELETE", `${base}?confirm=true`, undefined, returnTo, t("detail.failed"));
            if (result.ok) router.push("/admin/connections");
            else setMessages({ ok: false, list: result.messages });
          }}
        />
      </section>
    </div>
  );
}
