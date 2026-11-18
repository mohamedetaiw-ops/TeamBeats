"use client";

import Link from "next/link";
import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { NumberField } from "@/components/ui/Inputs";
import { FactList } from "@/components/ui/Patterns";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { IconButton } from "@/components/ui/IconButton";
import { unitLabel, type DirectoryView, type GroupRoleMapping, type IdentityProvider, type OrgUnitView } from "@/lib/admin/types";
import { ResultAlert, useAdminAction, useWhen } from "./shared";

const RETURN = "/admin/identity";
const SOURCES = ["entra", "adds", "google"] as const;

const lines = (text: string) =>
  text
    .split(/[\n,]/u)
    .map((s) => s.trim())
    .filter(Boolean);

/**
 * Sign-in settings (spec 003 US2, US6): the workspace's Microsoft Entra ID sign-in, who may join on first sign-in, the
 * Entra groups that grant roles, and directory sync with its schedule, safety threshold and recent runs. Saving either
 * asks for a fresh sign-in, and the client secret is only ever a secret manager reference (spec 006 FR-012).
 */
export function IdentityAdmin({
  tenantId,
  initialProvider,
  initialDirectory,
  roles,
  units,
}: {
  tenantId: string;
  initialProvider: IdentityProvider | null;
  initialDirectory: DirectoryView;
  roles: { key: string; name: string }[];
  units: OrgUnitView[];
}) {
  const t = useTranslations("admin.identity");
  const locale = useLocale();
  const when = useWhen();
  const { busy, result, run } = useAdminAction(RETURN);
  const [provider, setProvider] = useState(initialProvider);
  const [displayName, setDisplayName] = useState(initialProvider?.displayName ?? "");
  const [issuer, setIssuer] = useState(initialProvider?.issuer ?? "");
  const [clientId, setClientId] = useState(initialProvider?.clientId ?? "");
  const [secretRef, setSecretRef] = useState(initialProvider?.secretRef ?? "");
  const [enabled, setEnabled] = useState(initialProvider?.enabled ?? true);
  const [jit, setJit] = useState(initialProvider?.jitEnabled ?? false);
  const [domains, setDomains] = useState((initialProvider?.jitDomains ?? []).join("\n"));
  const [mappings, setMappings] = useState<GroupRoleMapping[]>(initialProvider?.groupRoleMap ?? []);

  const [directory, setDirectory] = useState(initialDirectory);
  const settings = directory.settings;
  const [mode, setMode] = useState(settings?.userScope.mode ?? "all");
  const [scopeGroups, setScopeGroups] = useState((settings?.userScope.groups ?? []).join("\n"));
  const [syncGroups, setSyncGroups] = useState((settings?.syncGroups ?? []).join("\n"));
  const [schedule, setSchedule] = useState(settings?.scheduleMinutes ?? 30);
  const [threshold, setThreshold] = useState(settings?.deletionThresholdPct ?? 10);
  const activeUnits = units.filter((u) => u.status === "active");

  async function saveProvider() {
    const outcome = await run<IdentityProvider>(
      "provider",
      "PUT",
      "/api/v1/tenant/identity-provider",
      {
        kind: "entra",
        issuer: issuer.trim(),
        clientId: clientId.trim(),
        secretRef: secretRef.trim(),
        displayName: displayName.trim() || null,
        jitEnabled: jit,
        jitDomains: lines(domains),
        groupRoleMap: mappings.map((m) => ({ ...m, groupId: m.groupId.trim(), scopeId: m.scopeType === "tenant" ? null : m.scopeId })),
        enabled,
      },
      t("sso.saved"),
    );
    if (outcome.ok) setProvider(outcome.data);
  }

  async function reloadDirectory() {
    const response = await fetch("/api/v1/tenant/directory", { cache: "no-store" });
    if (response.ok) setDirectory((await response.json()) as DirectoryView);
  }

  async function saveDirectory() {
    const outcome = await run(
      "directory",
      "PUT",
      "/api/v1/tenant/directory",
      {
        userScope: { mode, groups: mode === "groups" ? lines(scopeGroups) : [] },
        syncGroups: lines(syncGroups),
        scheduleMinutes: schedule,
        deletionThresholdPct: threshold,
        // The Microsoft 365 source is found by the API itself; other sources name their connection.
        connectionId: settings && settings.source !== "entra" ? settings.connectionId : null,
      },
      t("directory.saved"),
    );
    if (outcome.ok) await reloadDirectory();
  }

  const setMapping = (i: number, change: Partial<GroupRoleMapping>) => setMappings(mappings.map((m, j) => (j === i ? { ...m, ...change } : m)));
  const source = settings?.source ?? "entra";

  return (
    <div className="admin-screen">
      <ResultAlert result={result} />

      <section className="admin-panel" aria-labelledby="sso-title">
        <div className="admin-panel-head">
          <h2 id="sso-title">{t("sso.title")}</h2>
          {provider ? <Badge tone={provider.enabled ? "success" : "neutral"}>{t(provider.enabled ? "sso.on" : "sso.off")}</Badge> : <Badge>{t("sso.notSet")}</Badge>}
        </div>
        <p className="admin-meta">{t("sso.intro")}</p>
        <form
          className="admin-form"
          onSubmit={(e) => {
            e.preventDefault();
            void saveProvider();
          }}
        >
          <FormField label={t("sso.displayName")} hint={t("sso.displayNameHint")} name="displayName" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
          <FormField label={t("sso.issuer")} hint={t("sso.issuerHint")} name="issuer" dir="ltr" required value={issuer} onChange={(e) => setIssuer(e.target.value)} />
          {/* Long identifiers get the full row so they read in full (DLS-097). */}
          <FormField label={t("sso.clientId")} hint={t("sso.clientIdHint")} name="clientId" dir="ltr" required value={clientId} onChange={(e) => setClientId(e.target.value)} />
          <FormField
            label={t("sso.secretRef")}
            hint={t("sso.secretRefHint", { prefix: `secret://tenants/${tenantId}/` })}
            name="secretRef"
            dir="ltr"
            required
            value={secretRef}
            onChange={(e) => setSecretRef(e.target.value)}
          />
          <Checkbox name="enabled" label={t("sso.enabled")} hint={t("sso.enabledHint")} checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />

          <fieldset className="admin-fieldset">
            <legend>{t("sso.jitTitle")}</legend>
            <Checkbox name="jit" label={t("sso.jit")} hint={t("sso.jitHint")} checked={jit} onChange={(e) => setJit(e.target.checked)} />
            {jit ? <TextAreaField label={t("sso.domains")} hint={t("sso.domainsHint")} name="domains" dir="ltr" rows={3} value={domains} onChange={(e) => setDomains(e.target.value)} /> : null}
          </fieldset>

          <fieldset className="admin-fieldset">
            <legend>{t("sso.groupsTitle")}</legend>
            <p className="admin-meta">{t("sso.groupsHint")}</p>
            {mappings.map((m, i) => (
              <div key={i} className="admin-mapping">
                <FormField label={t("sso.groupId")} name={`group-${i}`} dir="ltr" value={m.groupId} onChange={(e) => setMapping(i, { groupId: e.target.value })} />
                <SelectField label={t("sso.role")} name={`role-${i}`} value={m.roleKey} onChange={(e) => setMapping(i, { roleKey: e.target.value })} options={roles.map((r) => ({ value: r.key, label: r.name }))} />
                <SelectField
                  label={t("sso.scope")}
                  name={`scope-${i}`}
                  value={m.scopeType}
                  onChange={(e) => setMapping(i, { scopeType: e.target.value, scopeId: e.target.value === "tenant" ? null : (m.scopeId ?? activeUnits[0]?.id ?? null) })}
                  options={["tenant", "org_subtree", "org_unit"].map((s) => ({ value: s, label: t(`scopes.${s}`) }))}
                />
                {m.scopeType !== "tenant" ? (
                  <SelectField label={t("sso.unit")} name={`unit-${i}`} value={m.scopeId ?? ""} onChange={(e) => setMapping(i, { scopeId: e.target.value })} options={activeUnits.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))} />
                ) : null}
                <IconButton icon="trash" label={t("sso.removeMapping")} onClick={() => setMappings(mappings.filter((_, j) => j !== i))} />
              </div>
            ))}
            <div className="ui-control-row">
              <Button size="sm" icon="plus" onClick={() => setMappings([...mappings, { groupId: "", roleKey: roles[0]?.key ?? "employee", scopeType: "tenant", scopeId: null }])}>
                {t("sso.addMapping")}
              </Button>
            </div>
          </fieldset>
          <div className="ui-control-row">
            <Button type="submit" variant="primary" busy={busy === "provider"} busyLabel={t("saving")}>
              {t("sso.save")}
            </Button>
          </div>
          <p className="admin-meta">{t("stepUpHint")}</p>
        </form>
      </section>

      <section className="admin-panel" aria-labelledby="directory-title">
        <div className="admin-panel-head">
          <h2 id="directory-title">{t("directory.title")}</h2>
          {settings ? (
            <Badge tone={settings.consecutiveFailures > 0 ? "warning" : "success"}>{t(settings.consecutiveFailures > 0 ? "directory.failing" : "directory.on")}</Badge>
          ) : (
            <Badge>{t("directory.notSet")}</Badge>
          )}
        </div>
        <p className="admin-meta">{t("directory.intro")}</p>
        {!directory.connectionId && !settings ? (
          <Alert tone="info" title={t("directory.noConnection")} action={<Link href="/admin/connections">{t("directory.openConnections")}</Link>}>
            <p>{t("directory.noConnectionHint")}</p>
          </Alert>
        ) : (
          <>
            {!directory.connectionUsable && !settings ? <Alert tone="warning" title={t("directory.connectionNotReady")} /> : null}
            {settings ? (
              <FactList
                facts={[
                  { key: "source", label: t("directory.source"), value: t(`directory.sources.${SOURCES.includes(source as (typeof SOURCES)[number]) ? source : "entra"}`) },
                  { key: "last", label: t("directory.lastRun"), value: when(settings.lastRunAt) },
                  { key: "next", label: t("directory.nextRun"), value: when(settings.nextRunAt) },
                  { key: "failures", label: t("directory.failures"), value: settings.consecutiveFailures },
                ]}
              />
            ) : null}
            <form
              className="admin-form"
              onSubmit={(e) => {
                e.preventDefault();
                void saveDirectory();
              }}
            >
              <RadioGroup
                legend={t("directory.who")}
                name="userScope"
                value={mode}
                onChange={(e) => setMode(e.target.value)}
                options={[
                  { value: "all", label: t("directory.everyone") },
                  { value: "groups", label: t("directory.someGroups") },
                ]}
              />
              {mode === "groups" ? (
                <TextAreaField label={t("directory.scopeGroups")} hint={t("directory.groupsHint")} name="scopeGroups" dir="ltr" rows={3} value={scopeGroups} onChange={(e) => setScopeGroups(e.target.value)} />
              ) : null}
              <TextAreaField label={t("directory.syncGroups")} hint={t("directory.syncGroupsHint")} name="syncGroups" dir="ltr" rows={3} value={syncGroups} onChange={(e) => setSyncGroups(e.target.value)} />
              <div className="admin-fields">
                <NumberField label={t("directory.schedule")} hint={t("directory.scheduleHint")} name="schedule" min={5} max={1440} value={schedule} onValueChange={(v) => setSchedule(v ?? 30)} />
                <NumberField label={t("directory.threshold")} hint={t("directory.thresholdHint")} name="threshold" min={1} max={100} value={threshold} onValueChange={(v) => setThreshold(v ?? 10)} />
              </div>
              <div className="ui-control-row">
                <Button type="submit" variant="primary" busy={busy === "directory"} busyLabel={t("saving")}>
                  {t("directory.save")}
                </Button>
                {settings ? (
                  <Button
                    icon="clock"
                    busy={busy === "sync"}
                    busyLabel={t("saving")}
                    onClick={() => void run("sync", "POST", "/api/v1/tenant/directory/sync", undefined, t("directory.queued")).then(() => reloadDirectory())}
                  >
                    {t("directory.syncNow")}
                  </Button>
                ) : null}
              </div>
            </form>
            <DataTable
              caption={t("directory.runs")}
              rows={directory.runs}
              rowKey={(r) => r.id}
              emptyTitle={t("directory.noRuns")}
              density="compact"
              columns={[
                { key: "started", header: t("directory.started"), rowHeader: true, cell: (r) => when(r.startedAt, "short") },
                {
                  key: "outcome",
                  header: t("directory.outcome"),
                  cell: (r) => (
                    <Badge tone={r.outcome === "succeeded" ? "success" : r.outcome === "running" ? "info" : "danger"}>
                      {t.has(`directory.outcomes.${r.outcome}`) ? t(`directory.outcomes.${r.outcome}`) : r.outcome}
                    </Badge>
                  ),
                },
                { key: "created", header: t("directory.created"), align: "end", cell: (r) => r.created },
                { key: "updated", header: t("directory.updated"), align: "end", cell: (r) => r.updated },
                { key: "disabled", header: t("directory.disabled"), align: "end", cell: (r) => r.disabled },
                { key: "groups", header: t("directory.groups"), align: "end", cell: (r) => r.groups, hideable: true },
              ]}
            />
          </>
        )}
      </section>
    </div>
  );
}
