"use client";

import { useState } from "react";
import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { NumberField } from "@/components/ui/Inputs";
import { Switch } from "@/components/ui/Switch";
import { Tabs } from "@/components/ui/Tabs";
import { ResultAlert, useAdminAction, usePeopleNames, useWhen } from "@/features/admin/shared";
import {
  appKinds,
  isDelegated,
  scopeLabelKey,
  scopesFor,
  type AdminConsent,
  type ApiApp,
  type ApiScope,
  type ApiSettings,
  type ApiUsage,
  type AppKind,
  type DeveloperPage,
} from "@/lib/api-access";
import "./api-access.css";

const returnTo = "/admin/api";

/**
 * API and MCP access for API administrators (spec 035 US1, US5, US6, FR-013, FR-028, FR-029): the registered apps, every
 * consent in the tenant, the usage of each app, and the tenant's token, consent and limit settings. Viewers see the same pages; every change is
 * checked again by the API.
 */
export function ApiAccessAdmin({
  apps: initialApps,
  scopes,
  consents: initialConsents,
  settings: initialSettings,
  usage,
  developer = null,
  initialTab,
}: {
  apps: ApiApp[];
  scopes: ApiScope[];
  consents: AdminConsent[];
  settings: ApiSettings;
  usage: ApiUsage | null;
  developer?: DeveloperPage | null;
  initialTab?: string;
}) {
  const t = useTranslations("apiAccess.admin");
  const [apps, setApps] = useState(initialApps);
  const [consents, setConsents] = useState(initialConsents);
  const [settings, setSettings] = useState(initialSettings);
  const action = useAdminAction(returnTo);

  return (
    <div className="admin-screen">
      <ResultAlert result={action.result} />
      <Tabs
        label={t("tabs")}
        initialKey={initialTab}
        tabs={[
          {
            key: "apps",
            label: t("appsTab"),
            content: <AppsPanel apps={apps} scopes={scopes} action={action} onCreated={(app) => setApps((list) => [...list, app])} />,
          },
          {
            key: "consents",
            label: t("consentsTab"),
            content: (
              <ConsentsPanel consents={consents} action={action} onRevoked={(id) => setConsents((list) => list.filter((c) => c.consentId !== id))} />
            ),
          },
          {
            key: "usage",
            label: t("usageTab"),
            content: <UsagePanel usage={usage} />,
          },
          {
            key: "developer",
            label: t("developerTab"),
            content: <DeveloperPanel page={developer} />,
          },
          {
            key: "settings",
            label: t("settingsTab"),
            content: <SettingsPanel settings={settings} scopes={scopes} action={action} onSaved={setSettings} />,
          },
        ]}
      />
    </div>
  );
}

type Action = ReturnType<typeof useAdminAction>;

function AppsPanel({ apps, scopes, action, onCreated }: { apps: ApiApp[]; scopes: ApiScope[]; action: Action; onCreated: (app: ApiApp) => void }) {
  const t = useTranslations("apiAccess.admin");
  const when = useWhen();
  return (
    <div className="admin-screen">
      <section className="admin-panel" aria-labelledby="api-apps-title">
        <div className="admin-panel-head">
          <h2 id="api-apps-title">{t("appsTitle")}</h2>
        </div>
        <p className="admin-meta">{t("appsIntro")}</p>
        <DataTable
          caption={t("appsTitle")}
          captionHidden
          rows={apps}
          rowKey={(a) => a.id}
          emptyTitle={t("noApps")}
          emptyMessage={t("noAppsHint")}
          columns={[
            {
              key: "name",
              header: t("name"),
              rowHeader: true,
              cell: (a) => <Link href={`/admin/api/apps/${a.id}`}>{a.name}</Link>,
              sortValue: (a) => a.name,
            },
            {
              key: "kind",
              header: t("kind"),
              cell: (a) => (
                <>
                  {t(`kinds.${a.kind}`)} {a.verified ? <Badge tone="success">{t("verified")}</Badge> : null}
                </>
              ),
            },
            {
              key: "status",
              header: t("status"),
              cell: (a) => <Badge tone={a.status === "active" ? "success" : a.status === "pending" ? "warning" : a.status === "blocked" ? "danger" : "neutral"}>{t(`statuses.${a.status}`)}</Badge>,
            },
            { key: "lastUsed", header: t("lastUsed"), cell: (a) => when(a.lastUsedAt) },
          ]}
        />
      </section>
      <RegisterApp scopes={scopes} action={action} onCreated={onCreated} />
    </div>
  );
}

function RegisterApp({ scopes, action, onCreated }: { scopes: ApiScope[]; action: Action; onCreated: (app: ApiApp) => void }) {
  const t = useTranslations("apiAccess.admin");
  const describe = useTranslations("apiAccess.scope");
  const [name, setName] = useState("");
  const [kind, setKind] = useState<AppKind>("application");
  const [publisher, setPublisher] = useState("");
  const [description, setDescription] = useState("");
  const [chosen, setChosen] = useState<string[]>([]);
  const [redirects, setRedirects] = useState("");
  const offered = scopesFor(kind, scopes);

  async function register() {
    const outcome = await action.run<ApiApp>(
      "register",
      "POST",
      "/api/v1/api-access/apps",
      {
        name,
        kind,
        publisher,
        description,
        scopes: chosen.filter((s) => offered.some((o) => o.key === s)),
        redirectUris: isDelegated(kind) ? redirects.split(/\s+/).filter(Boolean) : [],
      },
      (app) => t("registered", { name: app.name }),
    );
    if (outcome.ok) {
      onCreated(outcome.data);
      setName("");
      setPublisher("");
      setDescription("");
      setChosen([]);
      setRedirects("");
    }
  }

  return (
    <section className="admin-panel" aria-labelledby="api-register-title">
      <h2 id="api-register-title">{t("registerTitle")}</h2>
      <form
        className="admin-form"
        onSubmit={(e) => {
          e.preventDefault();
          void register();
        }}
      >
        <div className="admin-fields">
          <FormField label={t("name")} name="name" required maxLength={80} value={name} onChange={(e) => setName(e.target.value)} />
          <SelectField
            label={t("kind")}
            hint={t(`kindHints.${kind}`)}
            name="kind"
            value={kind}
            onChange={(e) => setKind(e.target.value as AppKind)}
            options={appKinds.map((k) => ({ value: k, label: t(`kinds.${k}`) }))}
          />
        </div>
        <FormField label={t("publisher")} hint={t("publisherHint")} name="publisher" maxLength={80} value={publisher} onChange={(e) => setPublisher(e.target.value)} />
        <TextAreaField label={t("description")} name="description" rows={2} value={description} onChange={(e) => setDescription(e.target.value)} />
        <fieldset className="admin-fieldset">
          <legend>{t("scopes")}</legend>
          <div className="api-access-scopes">
            {offered.map((s) => (
              <Checkbox
                key={s.key}
                name={`scope-${s.key}`}
                label={<code dir="ltr">{s.key}</code>}
                hint={describe(scopeLabelKey(s.key))}
                checked={chosen.includes(s.key)}
                onChange={(e) => setChosen((list) => (e.target.checked ? [...list, s.key] : list.filter((k) => k !== s.key)))}
              />
            ))}
          </div>
        </fieldset>
        {isDelegated(kind) ? (
          <TextAreaField
            label={t("redirects")}
            hint={t("redirectsHint")}
            name="redirects"
            dir="ltr"
            rows={3}
            value={redirects}
            onChange={(e) => setRedirects(e.target.value)}
          />
        ) : null}
        <div className="ui-control-row">
          <Button type="submit" variant="primary" busy={action.busy === "register"} busyLabel={t("saving")}>
            {t("register")}
          </Button>
        </div>
      </form>
    </section>
  );
}

function ConsentsPanel({ consents, action, onRevoked }: { consents: AdminConsent[]; action: Action; onRevoked: (id: string) => void }) {
  const t = useTranslations("apiAccess.admin");
  const when = useWhen();
  const person = usePeopleNames(consents.map((c) => c.userId));
  return (
    <section className="admin-panel" aria-labelledby="api-consents-title">
      <h2 id="api-consents-title">{t("consentsTitle")}</h2>
      <p className="admin-meta">{t("consentsIntro")}</p>
      <DataTable
        caption={t("consentsTitle")}
        captionHidden
        rows={consents}
        rowKey={(c) => c.consentId}
        emptyTitle={t("noConsents")}
        columns={[
          { key: "app", header: t("app"), rowHeader: true, cell: (c) => <Link href={`/admin/api/apps/${c.appId}`}>{c.appName}</Link>, sortValue: (c) => c.appName },
          { key: "who", header: t("who"), cell: (c) => (c.userId ? person(c.userId) : <Badge tone="info">{t("wholeOrganization")}</Badge>) },
          {
            key: "scopes",
            header: t("scopes"),
            cell: (c) => (
              <span className="api-access-codes" dir="ltr">
                {c.scopes.join(" ")}
              </span>
            ),
          },
          { key: "granted", header: t("granted"), cell: (c) => when(c.grantedAt, "short"), sortValue: (c) => c.grantedAt },
          { key: "lastUsed", header: t("lastUsed"), cell: (c) => when(c.lastUsedAt, "short") },
          {
            key: "actions",
            header: t("actions"),
            align: "end",
            cell: (c) =>
              c.userId ? (
                <ConfirmAction
                  label={t("revoke")}
                  question={t("revokeConsentQuestion", { app: c.appName, person: person(c.userId) })}
                  confirmLabel={t("revoke")}
                  cancelLabel={t("cancel")}
                  onConfirm={async () => {
                    const outcome = await action.run("consent-" + c.consentId, "POST", `/api/v1/api-access/consents/${c.consentId}:revoke`, undefined, t("consentRevoked"));
                    if (outcome.ok) onRevoked(c.consentId);
                  }}
                />
              ) : (
                <Link href={`/admin/api/apps/${c.appId}`}>{t("changeAdminConsent")}</Link>
              ),
          },
        ]}
      />
    </section>
  );
}

function SettingsPanel({ settings, scopes, action, onSaved }: { settings: ApiSettings; scopes: ApiScope[]; action: Action; onSaved: (s: ApiSettings) => void }) {
  const t = useTranslations("apiAccess.admin");
  const describe = useTranslations("apiAccess.scope");
  const [enabled, setEnabled] = useState(settings.apiEnabled);
  const [minutes, setMinutes] = useState<number | null>(settings.accessTokenMinutes);
  const [days, setDays] = useState<number | null>(settings.refreshTokenDays);
  const [userScopes, setUserScopes] = useState(settings.userConsentScopes);
  const [tenantLimit, setTenantLimit] = useState<number | null>(settings.tenantCallsPerMinute);
  const [dynamic, setDynamic] = useState(settings.dynamicRegistration ?? false);
  const [reason, setReason] = useState("");
  const delegated = scopes.filter((s) => s.holder !== "application");

  async function save() {
    const outcome = await action.run<ApiSettings>(
      "settings",
      "PUT",
      "/api/v1/api-access/settings",
      { apiEnabled: enabled, accessTokenMinutes: minutes, refreshTokenDays: days, userConsentScopes: userScopes, tenantCallsPerMinute: tenantLimit ?? 0, dynamicRegistration: dynamic, reason, version: settings.version },
      t("settingsSaved"),
    );
    if (outcome.ok) onSaved(outcome.data);
  }

  return (
    <section className="admin-panel" aria-labelledby="api-settings-title">
      <h2 id="api-settings-title">{t("settingsTitle")}</h2>
      <form
        className="admin-form"
        onSubmit={(e) => {
          e.preventDefault();
          void save();
        }}
      >
        <Switch name="apiEnabled" label={t("apiEnabled")} hint={t("apiEnabledHint")} checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        {settings.apiEnabled && !enabled ? (
          <TextAreaField label={t("switchReason")} hint={t("switchReasonHint")} name="reason" rows={2} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />
        ) : null}
        <Switch
          name="dynamicRegistration"
          label={t("dynamicRegistration")}
          hint={t("dynamicRegistrationHint")}
          checked={dynamic}
          onChange={(e) => setDynamic(e.target.checked)}
        />
        <div className="admin-fields">
          <NumberField label={t("accessTokenMinutes")} hint={t("accessTokenMinutesHint")} name="accessTokenMinutes" min={5} max={60} value={minutes} onValueChange={setMinutes} />
          <NumberField label={t("refreshTokenDays")} hint={t("refreshTokenDaysHint")} name="refreshTokenDays" min={1} max={30} value={days} onValueChange={setDays} />
          <NumberField
            label={t("tenantCallsPerMinute")}
            hint={t("tenantCallsPerMinuteHint")}
            name="tenantCallsPerMinute"
            min={1}
            value={tenantLimit}
            onValueChange={setTenantLimit}
          />
        </div>
        <fieldset className="admin-fieldset">
          <legend>{t("userConsent")}</legend>
          <p className="admin-meta">{t("userConsentHint")}</p>
          <div className="api-access-scopes">
            {delegated.map((s) => (
              <Checkbox
                key={s.key}
                name={`user-${s.key}`}
                label={<code dir="ltr">{s.key}</code>}
                hint={describe(scopeLabelKey(s.key))}
                checked={userScopes.includes(s.key)}
                onChange={(e) => setUserScopes((list) => (e.target.checked ? [...list, s.key] : list.filter((k) => k !== s.key)))}
              />
            ))}
          </div>
        </fieldset>
        <div className="ui-control-row">
          <Button type="submit" variant="primary" busy={action.busy === "settings"} busyLabel={t("saving")}>
            {t("saveSettings")}
          </Button>
        </div>
      </form>
    </section>
  );
}

function UsagePanel({ usage }: { usage: ApiUsage | null }) {
  const t = useTranslations("apiAccess.admin");
  const when = useWhen();
  const format = useFormatter();
  if (!usage) {
    return <p className="admin-meta">{t("usageUnavailable")}</p>;
  }
  const errorCount = (errors: Record<string, number>) =>
    Object.entries(errors)
      .filter(([kind]) => kind !== "rate_limited" && kind !== "quota")
      .reduce((sum, [, n]) => sum + n, 0);
  return (
    <section className="admin-panel" aria-labelledby="api-usage-title">
      <h2 id="api-usage-title">{t("usageTitle")}</h2>
      <p className="admin-meta">{t("usageIntro", { days: usage.days })}</p>
      <p>
        {usage.monthlyLimit === null
          ? t("monthCallsUnlimited", { calls: format.number(usage.monthCalls) })
          : t("monthCalls", { calls: format.number(usage.monthCalls), limit: format.number(usage.monthlyLimit) })}
      </p>
      <DataTable
        caption={t("usageTitle")}
        captionHidden
        rows={usage.apps}
        rowKey={(a) => a.appId}
        emptyTitle={t("noApps")}
        columns={[
          { key: "app", header: t("app"), rowHeader: true, cell: (a) => <Link href={`/admin/api/apps/${a.appId}`}>{a.name}</Link>, sortValue: (a) => a.name },
          { key: "calls", header: t("calls"), align: "end", cell: (a) => format.number(a.calls), sortValue: (a) => a.calls },
          { key: "errors", header: t("errors"), align: "end", cell: (a) => format.number(errorCount(a.errors)), sortValue: (a) => errorCount(a.errors) },
          { key: "denied", header: t("denied"), align: "end", cell: (a) => format.number(a.denied), sortValue: (a) => a.denied },
          { key: "limited", header: t("rateLimited"), align: "end", cell: (a) => format.number(a.rateLimited), sortValue: (a) => a.rateLimited },
          {
            key: "top",
            header: t("topOperations"),
            cell: (a) =>
              a.topOperations.length === 0 ? (
                t("none")
              ) : (
                <ul className="api-access-operations" dir="ltr">
                  {a.topOperations.map((o) => (
                    <li key={o.operation}>
                      <code>{o.operation}</code> {format.number(o.calls)}
                    </li>
                  ))}
                </ul>
              ),
          },
          { key: "last", header: t("lastCall"), cell: (a) => when(a.lastCallAt, "short"), sortValue: (a) => a.lastCallAt ?? "" },
        ]}
      />
    </section>
  );
}

/**
 * The developer page (spec 035 US7, FR-014): where the public OpenAPI document, the authorization server metadata and
 * the MCP server are, and the operations, scopes, events, MCP tools and resources the package includes. Operations,
 * paths and catalogue names stay in English; descriptions are in the reader's language.
 */
/** An operation as developers write it: method and path. */
const operationName = (o: { method: string; path: string }) => [o.method, o.path].join(" ");

function DeveloperPanel({ page }: { page: DeveloperPage | null }) {
  const t = useTranslations("apiAccess.admin");
  const describe = useTranslations("apiAccess");
  const format = useFormatter();
  if (!page) {
    return <p className="admin-meta">{t("developerUnavailable")}</p>;
  }
  const day = (value: string) => format.dateTime(new Date(value + "T00:00:00Z"), { dateStyle: "long", timeZone: "UTC" });
  const tool = (name: string, fallback: string | null) => (describe.has(`tool.${name}` as never) ? describe(`tool.${name}` as never) : (fallback ?? ""));
  return (
    <div className="admin-screen">
      <section className="admin-panel" aria-labelledby="api-developer-title">
        <h2 id="api-developer-title">{t("developerTitle")}</h2>
        <p className="admin-meta">{t("developerIntro")}</p>
        <dl className="api-access-addresses">
          {(
            [
              ["openApiUrl", page.openApiUrl],
              ["metadataUrl", page.authorizationServerMetadataUrl],
              ["mcpUrl", page.mcpUrl],
              ["resourceMetadataUrl", page.protectedResourceMetadataUrl],
            ] as const
          ).map(([key, url]) => (
            <div key={key}>
              <dt>{t(key)}</dt>
              <dd dir="ltr">
                <code>{url}</code>
              </dd>
            </div>
          ))}
        </dl>
      </section>
      <section className="admin-panel" aria-labelledby="api-developer-deprecations">
        <h2 id="api-developer-deprecations">{t("deprecationsTitle")}</h2>
        <DataTable
          caption={t("deprecationsTitle")}
          captionHidden
          rows={page.deprecations}
          rowKey={(o) => `${o.method} ${o.path}`}
          emptyTitle={t("noDeprecations")}
          emptyMessage={t("noDeprecationsHint")}
          columns={[
            { key: "operation", header: t("operation"), rowHeader: true, cell: (o) => <code dir="ltr">{operationName(o)}</code> },
            { key: "replacement", header: t("replacement"), cell: (o) => (o.deprecation?.replacement ? <code dir="ltr">{o.deprecation.replacement}</code> : t("none")) },
            { key: "sunset", header: t("sunset"), cell: (o) => (o.deprecation ? day(o.deprecation.sunset) : "") },
          ]}
        />
      </section>
      <section className="admin-panel" aria-labelledby="api-developer-operations">
        <h2 id="api-developer-operations">{t("operationsTitle")}</h2>
        <DataTable
          caption={t("operationsTitle")}
          captionHidden
          rows={page.operations}
          rowKey={(o) => `${o.method} ${o.path}`}
          emptyTitle={t("noOperations")}
          columns={[
            { key: "operation", header: t("operation"), rowHeader: true, cell: (o) => <code dir="ltr">{operationName(o)}</code> },
            { key: "scope", header: t("scope"), cell: (o) => <code dir="ltr">{o.scope}</code> },
            {
              key: "state",
              header: t("status"),
              cell: (o) => (o.deprecation ? <Badge tone="warning">{t("deprecated")}</Badge> : <Badge tone="success">{t("current")}</Badge>),
            },
          ]}
        />
      </section>
      <section className="admin-panel" aria-labelledby="api-developer-scopes">
        <h2 id="api-developer-scopes">{t("scopesTitle")}</h2>
        <DataTable
          caption={t("scopesTitle")}
          captionHidden
          rows={page.scopes}
          rowKey={(s) => s.key}
          emptyTitle={t("noOperations")}
          columns={[
            { key: "scope", header: t("scope"), rowHeader: true, cell: (s) => <code dir="ltr">{s.key}</code> },
            { key: "description", header: t("description"), cell: (s) => describe(s.descriptionKey.replace(/^apiAccess\./, "") as never) },
            { key: "holder", header: t("holder"), cell: (s) => t(`holders.${s.holder}`) },
          ]}
        />
      </section>
      <section className="admin-panel" aria-labelledby="api-developer-events">
        <h2 id="api-developer-events">{t("eventsTitle")}</h2>
        <p className="admin-meta">{t("eventsIntro")}</p>
        <DataTable
          caption={t("eventsTitle")}
          captionHidden
          rows={page.events}
          rowKey={(e) => e.type}
          emptyTitle={t("noEvents")}
          columns={[
            { key: "event", header: t("eventType"), rowHeader: true, cell: (e) => <code dir="ltr">{e.type}</code> },
            { key: "version", header: t("version"), align: "end", cell: (e) => format.number(e.version) },
            { key: "scope", header: t("scope"), cell: (e) => <code dir="ltr">{e.readScope}</code> },
          ]}
        />
      </section>
      <section className="admin-panel" aria-labelledby="api-developer-mcp">
        <h2 id="api-developer-mcp">{t("mcpTitle")}</h2>
        <DataTable
          caption={t("mcpTitle")}
          captionHidden
          rows={page.mcp}
          rowKey={(m) => `${m.kind} ${m.name}`}
          emptyTitle={t("noMcp")}
          columns={[
            { key: "name", header: t("name"), rowHeader: true, cell: (m) => <code dir="ltr">{m.name}</code> },
            { key: "kind", header: t("kind"), cell: (m) => t(`mcpKinds.${m.kind}`) },
            { key: "description", header: t("description"), cell: (m) => (m.kind === "resource" ? "" : tool(m.name, m.description)) },
            { key: "scope", header: t("scope"), cell: (m) => <code dir="ltr">{m.scope}</code> },
          ]}
        />
      </section>
    </div>
  );
}
