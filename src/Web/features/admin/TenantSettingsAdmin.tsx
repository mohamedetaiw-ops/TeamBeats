"use client";

import { useMemo, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";
import { Checkbox } from "@/components/ui/Checkbox";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, SelectField } from "@/components/ui/FormField";
import { ColorField } from "@/components/ui/Inputs";
import { FileUpload, type UploadItem } from "@/components/ui/FileUpload";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { Badge } from "@/components/ui/Badge";
import { FactList } from "@/components/ui/Patterns";
import type { AdminAppointment, TenantSettings } from "@/lib/admin/types";
import { ResultAlert, useAdminAction, useWhen } from "./shared";

const LOCALES = ["en", "ar", "fr", "ur", "hi"] as const;
const LAUNCH = new Set(["en", "ar"]);
const RETURN = "/admin/tenant";

function zones(current: string): string[] {
  let list: string[] = [];
  try {
    list = (Intl as unknown as { supportedValuesOf?: (k: string) => string[] }).supportedValuesOf?.("timeZone") ?? [];
  } catch {
    list = [];
  }
  return list.includes(current) ? list : [current, ...list];
}

/**
 * Workspace settings (spec 001 US2): languages, time zone, numbering, the R0 primary colour and logo, and the tenant
 * administrators (spec 001 US3). The save sends the version the screen read, so a change made meanwhile by another
 * administrator is refused instead of overwritten.
 */
export function TenantSettingsAdmin({ initial, initialAdmins }: { initial: TenantSettings; initialAdmins: AdminAppointment[] | null }) {
  const t = useTranslations("admin.tenant");
  const tl = useTranslations("locale");
  const when = useWhen();
  const { busy, result, run, setResult } = useAdminAction(RETURN);
  const [settings, setSettings] = useState(initial);
  const [enabled, setEnabled] = useState<string[]>(initial.enabledLocales);
  const [defaultLocale, setDefaultLocale] = useState(initial.defaultLocale);
  const [timeZone, setTimeZone] = useState(initial.timeZone);
  const [numbering, setNumbering] = useState(initial.numberingSystem);
  const [color, setColor] = useState(initial.primaryColor ?? "");
  const [logo, setLogo] = useState<UploadItem[]>([]);
  const [logoVersion, setLogoVersion] = useState(0);
  const [admins, setAdmins] = useState(initialAdmins);
  const [email, setEmail] = useState("");
  const zoneList = useMemo(() => zones(initial.timeZone), [initial.timeZone]);

  const dirty =
    defaultLocale !== settings.defaultLocale ||
    timeZone !== settings.timeZone ||
    numbering !== settings.numberingSystem ||
    (color || null) !== settings.primaryColor ||
    [...enabled].sort().join() !== [...settings.enabledLocales].sort().join();

  function toggleLocale(locale: string, on: boolean) {
    const next = on ? [...enabled, locale] : enabled.filter((l) => l !== locale);
    setEnabled(next);
    if (!next.includes(defaultLocale) && next[0]) setDefaultLocale(next[0]);
  }

  async function save() {
    const outcome = await run<TenantSettings>(
      "save",
      "PUT",
      "/api/v1/tenant/settings",
      { defaultLocale, enabledLocales: enabled, timeZone, numberingSystem: numbering, primaryColor: color || null, rowVersion: settings.rowVersion },
      t("saved"),
    );
    if (outcome.ok) {
      setSettings(outcome.data);
    }
  }

  async function uploadLogo(files: File[]) {
    const file = files[0];
    if (!file) return;
    setLogo([{ key: file.name, name: file.name, size: file.size, progress: 50 }]);
    setResult(null);
    try {
      const response = await fetch("/api/v1/tenant/branding/logo", { method: "PUT", headers: { "content-type": file.type || "application/octet-stream" }, body: file });
      if (response.ok) {
        setLogo([]);
        setLogoVersion((v) => v + 1);
        setSettings((s) => ({ ...s, hasLogo: true, rowVersion: s.rowVersion + 1 }));
        setResult({ ok: true, messages: [t("logoSaved")] });
      } else {
        const problem = (await response.json().catch(() => null)) as { errors?: Record<string, { message: string }[]> } | null;
        const message = Object.values(problem?.errors ?? {}).flat()[0]?.message ?? t("logoFailed");
        setLogo([{ key: file.name, name: file.name, size: file.size, error: message }]);
      }
    } catch {
      setLogo([{ key: file.name, name: file.name, size: file.size, error: t("logoFailed") }]);
    }
  }

  async function appoint() {
    const outcome = await run<AdminAppointment>("appoint", "POST", "/api/v1/tenant/admins", { email: email.trim() }, t("admins.appointed"));
    if (outcome.ok) {
      setEmail("");
      const list = await fetch("/api/v1/tenant/admins", { cache: "no-store" });
      if (list.ok) setAdmins((await list.json()) as AdminAppointment[]);
    }
  }

  return (
    <div className="admin-screen">
      <ResultAlert result={result} />

      <Card title={t("workspace.title")} headingLevel={2}>
        <FactList
          facts={[
            { key: "name", label: t("workspace.name"), value: <bdi>{settings.displayName}</bdi> },
            { key: "logo", label: t("workspace.logo"), value: settings.hasLogo ? t("workspace.hasLogo") : t("workspace.noLogo") },
          ]}
        />
        <p className="admin-meta">{t("workspace.nameHint")}</p>
      </Card>

      <section className="admin-panel" aria-labelledby="tenant-language-title">
        <h2 id="tenant-language-title">{t("language.title")}</h2>
        <p className="admin-meta">{t("language.intro")}</p>
        <fieldset className="admin-fieldset">
          <legend>{t("language.enabled")}</legend>
          <div className="admin-choices">
            {LOCALES.map((l) => (
              <Checkbox
                key={l}
                name="enabledLocales"
                label={tl(l)}
                hint={LAUNCH.has(l) ? undefined : t("language.pending")}
                checked={enabled.includes(l)}
                onChange={(e) => toggleLocale(l, e.target.checked)}
              />
            ))}
          </div>
        </fieldset>
        <div className="admin-fields">
          <SelectField
            label={t("language.default")}
            name="defaultLocale"
            value={defaultLocale}
            onChange={(e) => setDefaultLocale(e.target.value)}
            options={enabled.map((l) => ({ value: l, label: tl(l as (typeof LOCALES)[number]) }))}
          />
          <SelectField label={t("language.timeZone")} name="timeZone" value={timeZone} onChange={(e) => setTimeZone(e.target.value)} options={zoneList.map((z) => ({ value: z, label: z }))} />
        </div>
        <RadioGroup
          legend={t("language.numbering")}
          name="numberingSystem"
          value={numbering}
          onChange={(e) => setNumbering(e.target.value)}
          options={[
            { value: "latn", label: t("language.latn"), hint: "0123456789" },
            { value: "arab", label: t("language.arab"), hint: "٠١٢٣٤٥٦٧٨٩" },
          ]}
        />
        <ColorField label={t("language.color")} hint={t("language.colorHint")} name="primaryColor" value={color} onValueChange={setColor} />
        <div className="ui-control-row">
          <Button variant="primary" disabled={!dirty || enabled.length === 0} busy={busy === "save"} busyLabel={t("saving")} onClick={() => void save()}>
            {t("save")}
          </Button>
        </div>
      </section>

      <section className="admin-panel" aria-labelledby="tenant-logo-title">
        <h2 id="tenant-logo-title">{t("logo.title")}</h2>
        <p className="admin-meta">{t("logo.intro")}</p>
        {settings.hasLogo ? (
          // eslint-disable-next-line @next/next/no-img-element
          <img className="admin-logo" src={`/api/v1/tenant/branding/logo?v=${settings.rowVersion}-${logoVersion}`} alt={t("logo.current", { name: settings.displayName })} />
        ) : null}
        <FileUpload label={t("logo.choose")} hint={t("logo.hint")} accept="image/png,image/jpeg,image/svg+xml" multiple={false} items={logo} onAdd={(f) => void uploadLogo(f)} onRemove={() => setLogo([])} />
      </section>

      {admins ? (
        <section className="admin-panel" aria-labelledby="tenant-admins-title">
          <h2 id="tenant-admins-title">{t("admins.title")}</h2>
          <p className="admin-meta">{t("admins.intro")}</p>
          <DataTable
            caption={t("admins.title")}
            captionHidden
            rows={admins}
            rowKey={(a) => a.id}
            emptyTitle={t("admins.empty")}
            columns={[
              { key: "email", header: t("admins.email"), rowHeader: true, cell: (a) => <bdi>{a.email}</bdi>, sortValue: (a) => a.email },
              {
                key: "status",
                header: t("admins.status"),
                cell: (a) => <Badge tone={a.status === "active" ? "success" : a.status === "revoked" ? "neutral" : "info"}>{t.has(`admins.statuses.${a.status}`) ? t(`admins.statuses.${a.status}`) : a.status}</Badge>,
              },
              { key: "created", header: t("admins.created"), cell: (a) => when(a.createdAt), sortValue: (a) => a.createdAt },
            ]}
          />
          <form
            className="ui-control-row"
            onSubmit={(e) => {
              e.preventDefault();
              void appoint();
            }}
          >
            <FormField label={t("admins.newEmail")} name="adminEmail" type="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
            <Button type="submit" busy={busy === "appoint"} busyLabel={t("saving")} disabled={!email.trim()}>
              {t("admins.appoint")}
            </Button>
          </form>
          <p className="admin-meta">{t("admins.newHint")}</p>
        </section>
      ) : null}
    </div>
  );
}
