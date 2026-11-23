"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, SelectField } from "@/components/ui/FormField";
import { ResultAlert, useAdminAction } from "@/features/admin/shared";
import type { BrandAssignment, ThemeLock, TokenSchemaEntry, TokenValue } from "@/lib/brand/types";

type Scope = { value: string; label: string };
type Override = { token: string; value: TokenValue };

/**
 * Organization themes (spec 033 FR-002, FR-019): the brand each scope uses, the token values it sets for itself within
 * its parents' locks, and the locks it sets for everything below it. The API decides who may see and change each part and
 * refuses a value a parent locked; this screen shows its answers and sends what was chosen.
 */
export function OrganizationThemes({
  scopes,
  brands,
  tokens,
  shellSettings,
  initialScope,
  initialAssignment,
  initialLocks,
}: {
  scopes: Scope[];
  brands: { id: string; name: string }[];
  tokens: TokenSchemaEntry[];
  shellSettings: string[];
  initialScope: string;
  initialAssignment: BrandAssignment | null;
  initialLocks: ThemeLock[] | null;
}) {
  const t = useTranslations("brand.organizations");
  const tb = useTranslations("brand");
  const { busy, result, setResult, run } = useAdminAction("/admin/branding/organizations");
  const [scope, setScope] = useState(initialScope);
  const [assignment, setAssignment] = useState(initialAssignment);
  const [brandId, setBrandId] = useState(initialAssignment?.brandId ?? "");
  const [overrides, setOverrides] = useState<Override[]>(toList(initialAssignment));
  const [locks, setLocks] = useState(initialLocks);
  const [draft, setDraft] = useState({ token: "", value: "" });
  const [lockDraft, setLockDraft] = useState("");

  const scopeLabel = (value: string) => scopes.find((s) => s.value === value)?.label ?? t("otherScope");
  const settingLabel = (setting: string) =>
    shellSettings.includes(setting) ? t(`shellSettings.${setting}`) : tokens.some((x) => x.name === setting) ? tb(`tokenNames.${setting}`) : setting;
  const lockedAbove = new Set((locks ?? []).filter((l) => l.inherited).map((l) => l.setting));
  const own = (locks ?? []).filter((l) => !l.inherited).map((l) => l.setting);
  const isTenant = scope === "tenant";

  async function load(next: string) {
    setScope(next);
    setResult(null);
    setDraft({ token: "", value: "" });
    setLockDraft("");
    const path = encodeURIComponent(next);
    const [a, l] = await Promise.all([
      fetch(`/api/v1/experience/brand-assignments/${path}`, { cache: "no-store" }),
      fetch(`/api/v1/experience/locks/${path}`, { cache: "no-store" }),
    ]);
    const loaded = a.ok ? ((await a.json()) as BrandAssignment) : null;
    setAssignment(loaded);
    setBrandId(loaded?.brandId ?? "");
    setOverrides(toList(loaded));
    setLocks(l.ok ? ((await l.json()) as ThemeLock[]) : null);
  }

  async function saveTheme(next: Override[]) {
    const outcome = await run<BrandAssignment>(
      "theme",
      "PUT",
      `/api/v1/experience/brand-assignments/${encodeURIComponent(scope)}`,
      { brandId: brandId === "" ? null : brandId, overrides: Object.fromEntries(next.map((o) => [o.token, o.value])) },
      t("themeSaved", { scope: scopeLabel(scope) }),
    );
    if (outcome.ok) {
      setAssignment(outcome.data);
      setOverrides(toList(outcome.data));
      setDraft({ token: "", value: "" });
    }
  }

  async function saveLocks(next: string[], message: string) {
    const outcome = await run<ThemeLock[]>("locks", "PUT", `/api/v1/experience/locks/${encodeURIComponent(scope)}`, { settings: next }, message);
    if (outcome.ok) {
      setLocks(outcome.data);
      setLockDraft("");
    }
  }

  const addable = tokens.filter((x) => !lockedAbove.has(x.name) && !overrides.some((o) => o.token === x.name));
  const lockable = [...shellSettings, ...tokens.map((x) => x.name)].filter((s) => !(locks ?? []).some((l) => l.setting === s));
  const brandOptions = [...(isTenant ? [] : [{ value: "", label: t("inheritBrand") }]), ...brands.map((b) => ({ value: b.id, label: b.name }))];

  return (
    <div className="admin-screen">
      <ResultAlert result={result} />
      <SelectField label={t("scope")} name="scope" value={scope} onChange={(e) => void load(e.target.value)} options={scopes} />

      <section className="admin-panel" aria-labelledby="org-theme-title">
        <div className="admin-panel-head">
          <h2 id="org-theme-title">{t("themeTitle", { scope: scopeLabel(scope) })}</h2>
        </div>
        {assignment ? (
          <>
            <p className="admin-meta">{isTenant ? t("themeIntroTenant") : t("themeIntro")}</p>
            <SelectField label={t("brand")} name="brand" value={brandId} onChange={(e) => setBrandId(e.target.value)} options={brandOptions} />
            <DataTable
              caption={t("overridesTitle")}
              rows={overrides}
              rowKey={(o) => o.token}
              emptyTitle={t("overridesEmptyTitle")}
              emptyMessage={t("overridesEmptyMessage")}
              columns={[
                { key: "token", header: t("setting"), rowHeader: true, cell: (o) => settingLabel(o.token), sortValue: (o) => settingLabel(o.token) },
                { key: "name", header: t("name"), priority: "low", cell: (o) => <code dir="ltr">{o.token}</code> },
                { key: "value", header: t("value"), cell: (o) => <code dir="ltr">{o.value.$value}</code> },
                {
                  key: "actions",
                  header: t("actions"),
                  cell: (o) => (
                    <Button
                      variant="secondary"
                      disabled={busy !== null}
                      aria-label={t("removeOverrideFor", { setting: settingLabel(o.token) })}
                      onClick={() => setOverrides((list) => list.filter((x) => x.token !== o.token))}
                    >
                      {t("remove")}
                    </Button>
                  ),
                },
              ]}
            />
            <fieldset className="admin-fieldset">
              <legend>{t("addOverride")}</legend>
              <p className="admin-meta">{lockedAbove.size > 0 ? t("lockedHint", { count: lockedAbove.size }) : t("addHint")}</p>
              <div className="admin-fields">
                <SelectField
                  label={t("setting")}
                  name="override-token"
                  value={draft.token}
                  onChange={(e) => setDraft({ ...draft, token: e.target.value })}
                  options={[{ value: "", label: t("choose") }, ...addable.map((x) => ({ value: x.name, label: settingLabel(x.name) }))]}
                />
                <FormField
                  label={t("value")}
                  name="override-value"
                  dir="ltr"
                  hint={draft.token ? t("defaultValue", { value: tokens.find((x) => x.name === draft.token)?.light ?? "" }) : undefined}
                  value={draft.value}
                  onChange={(e) => setDraft({ ...draft, value: e.target.value })}
                />
              </div>
              <div className="ui-control-row">
                <Button
                  variant="secondary"
                  icon="plus"
                  disabled={busy !== null || draft.token === "" || draft.value.trim() === ""}
                  onClick={() => {
                    setOverrides((list) => [...list, { token: draft.token, value: { $value: draft.value.trim() } }]);
                    setDraft({ token: "", value: "" });
                  }}
                >
                  {t("add")}
                </Button>
              </div>
            </fieldset>
            <div className="ui-control-row">
              <Button variant="primary" disabled={busy !== null || (isTenant && brandId === "")} onClick={() => void saveTheme(overrides)}>
                {t("saveTheme")}
              </Button>
            </div>
          </>
        ) : (
          <p className="admin-meta">{t("themeUnavailable")}</p>
        )}
      </section>

      <section className="admin-panel" aria-labelledby="org-locks-title">
        <div className="admin-panel-head">
          <h2 id="org-locks-title">{t("locksTitle")}</h2>
        </div>
        {locks ? (
          <>
            <p className="admin-meta">{t("locksIntro")}</p>
            <DataTable
              caption={t("locksTitle")}
              captionHidden
              rows={locks}
              rowKey={(l) => l.setting}
              emptyTitle={t("locksEmptyTitle")}
              emptyMessage={t("locksEmptyMessage")}
              columns={[
                { key: "setting", header: t("setting"), rowHeader: true, cell: (l) => settingLabel(l.setting), sortValue: (l) => settingLabel(l.setting) },
                { key: "where", header: t("lockedAt"), cell: (l) => <bdi>{l.scope === "tenant" ? t("tenant") : scopeLabel(l.scope)}</bdi> },
                {
                  key: "state",
                  header: t("state"),
                  nowrap: true,
                  cell: (l) =>
                    l.permanent ? (
                      <Badge tone="info">{t("permanent")}</Badge>
                    ) : l.inherited ? (
                      <Badge tone="neutral">{t("inherited")}</Badge>
                    ) : (
                      <Badge tone="success">{t("setHere")}</Badge>
                    ),
                },
                {
                  key: "actions",
                  header: t("actions"),
                  cell: (l) =>
                    l.inherited || l.permanent ? null : (
                      <Button
                        variant="secondary"
                        disabled={busy !== null}
                        aria-label={t("unlockFor", { setting: settingLabel(l.setting) })}
                        onClick={() => void saveLocks(own.filter((s) => s !== l.setting), t("unlocked", { setting: settingLabel(l.setting) }))}
                      >
                        {t("unlock")}
                      </Button>
                    ),
                },
              ]}
            />
            <fieldset className="admin-fieldset">
              <legend>{t("addLock")}</legend>
              <div className="admin-fields">
                <SelectField
                  label={t("setting")}
                  name="lock-setting"
                  value={lockDraft}
                  onChange={(e) => setLockDraft(e.target.value)}
                  options={[{ value: "", label: t("choose") }, ...lockable.map((s) => ({ value: s, label: settingLabel(s) }))]}
                />
              </div>
              <div className="ui-control-row">
                <Button
                  variant="primary"
                  icon="lock"
                  disabled={busy !== null || lockDraft === ""}
                  onClick={() => void saveLocks([...own, lockDraft], t("locked", { setting: settingLabel(lockDraft) }))}
                >
                  {t("lock")}
                </Button>
              </div>
            </fieldset>
          </>
        ) : (
          <p className="admin-meta">{t("locksUnavailable")}</p>
        )}
      </section>
    </div>
  );
}

function toList(assignment: BrandAssignment | null): Override[] {
  return Object.entries(assignment?.overrides ?? {})
    .map(([token, value]) => ({ token, value }))
    .sort((a, b) => a.token.localeCompare(b.token));
}
