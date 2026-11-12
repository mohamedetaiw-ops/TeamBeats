"use client";

import { useRouter } from "next/navigation";
import { useState, type ReactNode } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button, ButtonLink } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { SearchField } from "@/components/ui/Inputs";
import { StateView } from "@/components/ui/StateView";
import { TileCard } from "@/components/ui/TileCard";
import { TYPE_GROUPS, isConnectionType, manageHref, typeLook, wizardHref, type ConnectionType } from "@/lib/integrations/catalogue";
import {
  M365_FEATURES,
  M365_PERMISSION_REASONS,
  TICKETING_KINDS,
  checkResult,
  isTicketing,
  known,
  type AllowlistEntry,
  type FeatureTestResult,
  type FeatureView,
  type Microsoft365Connection,
  type Microsoft365TestResult,
  type PackCheck,
  type PackConnection,
  type PackTestResult,
  type WebhookEventChoice,
} from "@/lib/integrations/types";
import { Allowlist } from "./Allowlist";
import { ClockSettingsForm, type ClockBody } from "./ClockSettingsForm";
import { ConnectionSettingsForm, type SettingsBody } from "./ConnectionSettingsForm";
import { FinanceSettingsForm } from "./FinanceSettingsForm";
import { LmsSettingsForm } from "./LmsSettingsForm";
import { GoogleSettingsForm, type GoogleBody } from "./GoogleSettingsForm";
import { HrRestSettingsForm, HrSftpSettingsForm, type HrSftpBody } from "./HrSettingsForm";
import { OnPremSettingsForm, type OnPremBody } from "./OnPremSettingsForm";
import { SlackSettingsForm, type SlackBody } from "./SlackSettingsForm";
import { WebhookSettingsForm, type WebhookBody } from "./WebhookSettingsForm";
import { Checks, Messages, ProductMark, StatusBadge, TestLine } from "./parts";
import { send } from "./request";

/** A connection type the wizard offers, with whether the tenant's package includes it (from the API). */
export type TypeChoice = { kind: ConnectionType; inPackage: boolean };

function useTypeText() {
  const t = useTranslations("connections");
  return {
    name: (kind: string) => (isConnectionType(kind) ? t(`kinds.${kind}`) : t("kinds.other")),
    description: (kind: string) => (kind === "custom_rest" ? t("catalogue.customRest") : isConnectionType(kind) ? t(`catalogue.${kind}`) : ""),
  };
}

/**
 * Step 1 (spec 006 FR-016): every connection type as a tile with its product mark, grouped by what it connects, with a
 * search by name. Types outside the package are listed apart with the reason and cannot be chosen.
 */
export function ChooseType({ types }: { types: TypeChoice[] }) {
  const t = useTranslations("connections");
  const text = useTypeText();
  // The page language, not the device's, so the server and the browser sort the same way.
  const locale = useLocale();
  const [query, setQuery] = useState("");
  const needle = query.trim().toLocaleLowerCase();
  const matches = (kind: string) => !needle || text.name(kind).toLocaleLowerCase().includes(needle) || text.description(kind).toLocaleLowerCase().includes(needle);
  const byName = (a: TypeChoice, b: TypeChoice) => text.name(a.kind).localeCompare(text.name(b.kind), locale);
  const available = types.filter((c) => c.inPackage && matches(c.kind)).sort(byName);
  const unavailable = types.filter((c) => !c.inPackage && matches(c.kind)).sort(byName);

  return (
    <section className="connections-section" aria-labelledby="choose-heading">
      <div className="connections-section-head">
        <h2 id="choose-heading">{t("wizard.choose.title")}</h2>
        <p>{t("wizard.choose.intro")}</p>
      </div>
      <SearchField label={t("wizard.choose.search")} value={query} onValueChange={setQuery} />
      {available.length === 0 && unavailable.length === 0 ? <StateView kind="empty" icon="search" title={t("wizard.choose.noMatch")} message="" /> : null}
      {TYPE_GROUPS.map((group) => {
        const inGroup = available.filter((c) => typeLook(c.kind).group === group);
        if (inGroup.length === 0) return null;
        return (
          <section key={group} className="connections-group" aria-labelledby={`group-${group}`}>
            <h3 id={`group-${group}`}>{t(`wizard.groups.${group}`)}</h3>
            <ul className="ui-tiles connections-types">
              {inGroup.map((c) => (
                <li key={c.kind}>
                  <TileCard
                    title={text.name(c.kind)}
                    href={wizardHref(c.kind)}
                    leading={<ProductMark kind={c.kind} size="lg" />}
                    meta={text.description(c.kind)}
                    headingLevel={4}
                    testId={`type-${c.kind}`}
                  />
                </li>
              ))}
            </ul>
          </section>
        );
      })}
      {unavailable.length > 0 ? (
        <section className="connections-group" aria-labelledby="group-unavailable">
          <h3 id="group-unavailable">{t("wizard.choose.unavailableTitle")}</h3>
          <p>{t("wizard.choose.unavailableNote")}</p>
          <ul className="connections-unavailable">
            {unavailable.map((c) => (
              <li key={c.kind} data-testid={`type-${c.kind}`}>
                <ProductMark kind={c.kind} size="sm" />
                <span>{text.name(c.kind)}</span>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </section>
  );
}

/** The Microsoft 365 settings: tenant ID, name and features, each with the Microsoft permissions it needs (US5 scenario 2). */
function Microsoft365SetupForm({ catalogue, submitLabel, busy, onSubmit }: { catalogue: FeatureView[]; submitLabel: string; busy: boolean; onSubmit: (body: Record<string, unknown>) => void }) {
  const t = useTranslations("connections");
  const [tenantId, setTenantId] = useState("");
  const [name, setName] = useState("");
  const [features, setFeatures] = useState<Set<string>>(new Set());
  return (
    <form
      className="connections-form"
      aria-label={t("m365.addTitle")}
      onSubmit={(e) => {
        e.preventDefault();
        onSubmit({ externalTenantId: tenantId.trim(), displayName: name.trim(), features: [...features] });
      }}
    >
      <FormField label={t("m365.tenantId")} hint={t("m365.tenantIdHint")} name="externalTenantId" dir="ltr" required value={tenantId} onChange={(e) => setTenantId(e.target.value)} />
      <FormField label={t("m365.name")} name="displayName" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
      <fieldset className="connections-fieldset">
        <legend>{t("m365.features")}</legend>
        {catalogue.map((f) => (
          <div key={f.key} className="connections-feature">
            <label className="connections-check">
              <input
                type="checkbox"
                disabled={!f.inPackage}
                checked={features.has(f.key)}
                onChange={(e) =>
                  setFeatures((s) => {
                    const next = new Set(s);
                    if (e.target.checked) next.add(f.key);
                    else next.delete(f.key);
                    return next;
                  })
                }
              />
              {known(M365_FEATURES, f.key) ? t(`m365.feature.${f.key}`) : f.key}
              {f.inPackage ? null : <> ({t("catalogue.notInPackage")})</>}
            </label>
            {f.permissions.length > 0 ? (
              <ul className="connections-permissions">
                {f.permissions.map((p) => {
                  const reason = checkResult(p.reasonKey);
                  return (
                    <li key={p.name}>
                      <bdi dir="ltr">{p.name}</bdi> {known(M365_PERMISSION_REASONS, reason) ? t(`m365.permissions.${reason}`) : null}
                    </li>
                  );
                })}
              </ul>
            ) : null}
          </div>
        ))}
      </fieldset>
      <Button type="submit" variant="primary" busy={busy} busyLabel={t("detail.working")}>
        {submitLabel}
      </Button>
    </form>
  );
}

function setupHeading(kind: ConnectionType): { title: string; intro: string } {
  switch (kind) {
    case "microsoft365":
      return { title: "m365.addTitle", intro: "m365.intro" };
    case "custom_rest":
      return { title: "add.title", intro: "add.intro" };
    case "servicenow":
    case "jira_service_management":
      return { title: `ticketing.${kind}.addTitle`, intro: `ticketing.${kind}.addIntro` };
    case "hr_rest":
      return { title: "hr.addRestTitle", intro: "hr.addIntro" };
    case "hr_sftp":
      return { title: "hr.addSftpTitle", intro: "hr.addIntro" };
    case "webhook":
      return { title: "webhooks.addTitle", intro: "webhooks.addIntro" };
    case "onprem_ad":
      return { title: "onprem.addTitle", intro: "onprem.addIntro" };
    case "clock_import":
      return { title: "clock.addTitle", intro: "clock.addIntro" };
    case "google_workspace":
      return { title: "google.addTitle", intro: "google.addIntro" };
    case "finance_rest":
      return { title: "finance.addTitle", intro: "finance.addIntro" };
    case "lms_rest":
      return { title: "lms.addTitle", intro: "lms.addIntro" };
    case "slack":
      return { title: "slack.addTitle", intro: "slack.addIntro" };
  }
}

type Body = SettingsBody | WebhookBody | OnPremBody | SlackBody | ClockBody | GoogleBody | HrSftpBody | Record<string, unknown>;

/**
 * Step 2: the settings of the chosen type. Saving creates the connection turned off (spec 017 US1) and moves on to the
 * test; the credential is a secret manager reference and the API asks for step-up before saving it.
 */
export function SetupStep({ kind, catalogue, webhookEvents }: { kind: ConnectionType; catalogue: FeatureView[]; webhookEvents: WebhookEventChoice[] }) {
  const t = useTranslations("connections");
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const heading = setupHeading(kind);
  const save = t("wizard.setup.save");
  const returnTo = wizardHref(kind);

  async function create(body: Body) {
    setBusy(true);
    setMessages([]);
    const m365 = kind === "microsoft365";
    const result = await send<{ id: string }>("POST", m365 ? "/api/v1/integrations/connections" : "/api/v1/integrations/pack-connections", { kind, ...body }, returnTo, t("failed"));
    if (result.ok) {
      router.push(wizardHref(kind, result.data.id, "test"));
      return;
    }
    setBusy(false);
    setMessages(result.messages);
  }

  let form: ReactNode;
  if (kind === "microsoft365") form = <Microsoft365SetupForm catalogue={catalogue} submitLabel={save} busy={busy} onSubmit={(b) => void create(b)} />;
  else if (isTicketing(kind))
    form = <ConnectionSettingsForm authTypes={TICKETING_KINDS[kind].auth} desk={TICKETING_KINDS[kind].desk} submitLabel={save} busy={busy} onSubmit={(b) => void create(b)} />;
  else if (kind === "slack") form = <SlackSettingsForm submitLabel={save} busy={busy} onSubmit={(b) => void create(b)} />;
  else if (kind === "clock_import") form = <ClockSettingsForm submitLabel={save} busy={busy} onSubmit={(b) => void create(b)} />;
  else if (kind === "google_workspace") form = <GoogleSettingsForm submitLabel={save} busy={busy} onSubmit={(b) => void create(b)} />;
  else if (kind === "finance_rest") form = <FinanceSettingsForm submitLabel={save} busy={busy} onSubmit={(b) => void create(b)} />;
  else if (kind === "lms_rest") form = <LmsSettingsForm submitLabel={save} busy={busy} onSubmit={(b) => void create(b)} />;
  else if (kind === "hr_rest") form = <HrRestSettingsForm submitLabel={save} busy={busy} onSubmit={(b) => void create(b)} />;
  else if (kind === "hr_sftp") form = <HrSftpSettingsForm submitLabel={save} busy={busy} onSubmit={(b) => void create(b)} />;
  else if (kind === "webhook") form = <WebhookSettingsForm events={webhookEvents} submitLabel={save} busy={busy} onSubmit={(b) => void create(b)} />;
  else if (kind === "onprem_ad") form = <OnPremSettingsForm submitLabel={save} busy={busy} onSubmit={(b) => void create(b)} />;
  else form = <ConnectionSettingsForm submitLabel={save} busy={busy} onSubmit={(b) => void create(b)} />;

  return (
    <section className="connections-section" aria-labelledby="setup-heading">
      <div className="connections-step-head">
        <ProductMark kind={kind} size="lg" />
        <div>
          <h2 id="setup-heading">{t(heading.title)}</h2>
          <p>{t(heading.intro)}</p>
        </div>
      </div>
      {form}
      <Messages messages={messages} />
      <p>
        <ButtonLink variant="quiet" icon="chevron-back" href={wizardHref()}>
          {t("wizard.setup.changeType")}
        </ButtonLink>
      </p>
    </section>
  );
}

/** Who the wizard is setting up, at the top of the test and turn on steps. */
function Saved({ kind, name, status }: { kind: string; name: string; status: string }) {
  const text = useTypeText();
  return (
    <div className="connections-step-head">
      <ProductMark kind={kind} size="lg" />
      <div>
        <p className="connections-entry-name">
          <bdi>{name}</bdi>
        </p>
        <p className="connections-entry-meta">
          <span>{text.name(kind)}</span>
          <StatusBadge status={status} />
        </p>
      </div>
    </div>
  );
}

function StepFooter({ kind, id, nextLabel }: { kind: string; id: string; nextLabel: string }) {
  const t = useTranslations("connections");
  return (
    <div className="connections-step-actions">
      <ButtonLink variant="quiet" href={manageHref(kind, id)}>
        {t("wizard.test.changeSettings")}
      </ButtonLink>
      <ButtonLink variant="primary" iconEnd="arrow-forward" href={wizardHref(kind, id, "enable")}>
        {nextLabel}
      </ButtonLink>
    </div>
  );
}

/**
 * Step 3 for connector packs: test the saved connection, read each check, and add a missing host to the allowlist in
 * place. Moving on does not need a passing test; the connection stays off until it is turned on.
 */
export function TestStep({ connection, allowlist }: { connection: PackConnection; allowlist: AllowlistEntry[] | null }) {
  const t = useTranslations("connections");
  const [checks, setChecks] = useState<PackCheck[]>(connection.lastTest?.checks ?? []);
  const [status, setStatus] = useState(connection.status);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<{ ok: boolean; list: string[] }>({ ok: true, list: [] });
  const returnTo = wizardHref(connection.kind, connection.id, "test");
  const outbound = connection.kind !== "onprem_ad";

  async function test() {
    setBusy(true);
    setMessages({ ok: true, list: [] });
    const result = await send<PackTestResult>("POST", `/api/v1/integrations/pack-connections/${connection.id}:test`, undefined, returnTo, t("detail.failed"));
    setBusy(false);
    if (result.ok) {
      setChecks(result.data.checks);
      setMessages({ ok: result.data.status === "ok", list: [t(result.data.status === "ok" ? "detail.testPassed" : "detail.testFailed")] });
      const again = await send<PackConnection>("GET", `/api/v1/integrations/pack-connections/${connection.id}`, undefined, returnTo, t("detail.failed"));
      if (again.ok) setStatus(again.data.status);
    } else {
      setMessages({ ok: false, list: result.messages });
    }
  }

  return (
    <>
      <section className="connections-section" aria-labelledby="test-heading">
        <Saved kind={connection.kind} name={connection.displayName} status={status} />
        <h2 id="test-heading">{t("wizard.test.title")}</h2>
        <p>{t("wizard.test.intro")}</p>
        <div className="connections-row">
          <Button busy={busy} busyLabel={t("detail.working")} onClick={() => void test()}>
            {t("detail.test")}
          </Button>
        </div>
        {checks.length > 0 ? <Checks checks={checks} /> : null}
        <Messages messages={messages.list} ok={messages.ok} />
        {checks.some((c) => !c.ok) ? <p>{t("wizard.test.failedNote")}</p> : null}
        <StepFooter kind={connection.kind} id={connection.id} nextLabel={t("wizard.test.continue")} />
      </section>
      {outbound ? <Allowlist initial={allowlist} returnTo={returnTo} /> : null}
    </>
  );
}

/** Step 3 for Microsoft 365: consent for each feature turned on, in Microsoft, then the test per feature. */
export function Microsoft365TestStep({ connection }: { connection: Microsoft365Connection }) {
  const t = useTranslations("connections");
  const base = `/api/v1/integrations/connections/${connection.id}`;
  const returnTo = wizardHref("microsoft365", connection.id, "test");
  const [busy, setBusy] = useState(false);
  const [tests, setTests] = useState<Record<string, FeatureTestResult>>({});
  const [messages, setMessages] = useState<{ ok: boolean; list: string[] }>({ ok: true, list: [] });
  const featureName = (key: string) => (known(M365_FEATURES, key) ? t(`m365.feature.${key}`) : key);
  const on = connection.features.filter((f) => f.enabled);

  async function test() {
    setBusy(true);
    setMessages({ ok: true, list: [] });
    const result = await send<Microsoft365TestResult>("POST", `${base}:test`, undefined, returnTo, t("detail.failed"));
    setBusy(false);
    if (result.ok) {
      setTests(Object.fromEntries(result.data.features.map((f) => [f.feature, f])));
      const passed = result.data.features.every((f) => f.ok);
      setMessages({ ok: passed, list: [t(passed ? "detail.testPassed" : "detail.testFailed")] });
    } else {
      setMessages({ ok: false, list: result.messages });
    }
  }

  return (
    <section className="connections-section" aria-labelledby="test-heading">
      <Saved kind="microsoft365" name={connection.displayName} status={connection.status} />
      <h2 id="test-heading">{t("wizard.test.consentTitle")}</h2>
      <p>{t("wizard.test.consentIntro")}</p>
      {on.length === 0 ? (
        <p className="notice">{t("wizard.test.noFeatures")}</p>
      ) : (
        <ul className="connections-list">
          {on.map((f) => (
            <li key={f.key} className="connections-item">
              <h3>{featureName(f.key)}</h3>
              <p className="connections-row">
                <span>{t(f.consentStatus === "granted" ? "m365.consent.granted" : "m365.consent.pending")}</span>
                {f.consentStatus !== "granted" ? (
                  <a className="button button-primary" href={`${base}/features/${f.key}/consent`}>
                    {t("m365.grantConsent", { feature: featureName(f.key) })}
                  </a>
                ) : null}
              </p>
              {f.key in tests ? <TestLine result={tests[f.key]!} /> : null}
            </li>
          ))}
        </ul>
      )}
      <div className="connections-row">
        <Button busy={busy} busyLabel={t("detail.working")} onClick={() => void test()}>
          {t("detail.test")}
        </Button>
      </div>
      <Messages messages={messages.list} ok={messages.ok} />
      <StepFooter kind="microsoft365" id={connection.id} nextLabel={t("wizard.test.continue")} />
    </section>
  );
}

/** Step 4: turn the connection on now, or leave it off and turn it on later from its page (spec 017 US1). */
export function EnableStep({ kind, id, name, status, enabled }: { kind: string; id: string; name: string; status: string; enabled: boolean }) {
  const t = useTranslations("connections");
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const base = kind === "microsoft365" ? `/api/v1/integrations/connections/${id}` : `/api/v1/integrations/pack-connections/${id}`;

  async function turnOn() {
    setBusy(true);
    setMessages([]);
    const result = await send<unknown>("POST", `${base}:enable`, undefined, wizardHref(kind, id, "enable"), t("detail.failed"));
    if (result.ok) {
      router.push(manageHref(kind, id));
      return;
    }
    setBusy(false);
    setMessages(result.messages);
  }

  return (
    <section className="connections-section" aria-labelledby="enable-heading">
      <Saved kind={kind} name={name} status={status} />
      <h2 id="enable-heading">{t("wizard.enable.title")}</h2>
      <p>{t(enabled ? "wizard.enable.alreadyOn" : "wizard.enable.intro", { name })}</p>
      <Messages messages={messages} />
      <div className="connections-step-actions">
        {enabled ? (
          <ButtonLink variant="primary" href={manageHref(kind, id)}>
            {t("wizard.enable.open")}
          </ButtonLink>
        ) : (
          <>
            <ButtonLink variant="quiet" href={manageHref(kind, id)}>
              {t("wizard.enable.later")}
            </ButtonLink>
            <Button variant="primary" busy={busy} busyLabel={t("detail.working")} onClick={() => void turnOn()}>
              {t("wizard.enable.turnOn")}
            </Button>
          </>
        )}
      </div>
    </section>
  );
}
