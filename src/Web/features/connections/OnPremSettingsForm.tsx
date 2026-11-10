"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import type { OnPremSettings, PackConnection } from "@/lib/integrations/types";

export type OnPremBody = { displayName: string; settings: OnPremSettings };

const PORTS = ["636", "3269"] as const;

/**
 * Settings of an on-premises Active Directory connection (spec 017 US6): the domain controller the sync agent reads
 * over LDAPS, the port, the base DN and optional LDAP filters. The service account and its password stay with the
 * agent and are never asked for here. The API checks every value again.
 */
export function OnPremSettingsForm({
  initial,
  submitLabel,
  busy,
  onSubmit,
}: {
  initial?: PackConnection;
  submitLabel: string;
  busy: boolean;
  onSubmit: (body: OnPremBody) => void;
}) {
  const t = useTranslations("connections");
  const portId = useId();
  const s = initial?.settings as unknown as OnPremSettings | undefined;
  const [name, setName] = useState(initial?.displayName ?? "");
  const [host, setHost] = useState(s?.host ?? "");
  const [port, setPort] = useState(String(s?.port ?? 636));
  const [baseDn, setBaseDn] = useState(s?.baseDn ?? "");
  const [userFilter, setUserFilter] = useState(s?.userFilter ?? "");
  const [groupFilter, setGroupFilter] = useState(s?.groupFilter ?? "");

  return (
    <form
      className="connections-form"
      aria-label={t("onprem.formLabel")}
      onSubmit={(e) => {
        e.preventDefault();
        onSubmit({
          displayName: name.trim(),
          settings: {
            host: host.trim() || null,
            port: Number(port),
            baseDn: baseDn.trim() || null,
            userFilter: userFilter.trim() || null,
            groupFilter: groupFilter.trim() || null,
          },
        });
      }}
    >
      <FormField label={t("onprem.name")} name="displayName" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
      <FormField label={t("onprem.host")} hint={t("onprem.hostHint")} name="host" dir="ltr" required value={host} onChange={(e) => setHost(e.target.value)} />
      <div className="field">
        <label htmlFor={portId}>{t("onprem.port")}</label>
        <select id={portId} value={port} onChange={(e) => setPort(e.target.value)}>
          {PORTS.map((p) => (
            <option key={p} value={p}>
              {t(`onprem.ports.${p}`)}
            </option>
          ))}
        </select>
      </div>
      <FormField label={t("onprem.baseDn")} hint={t("onprem.baseDnHint")} name="baseDn" dir="ltr" required value={baseDn} onChange={(e) => setBaseDn(e.target.value)} />
      <FormField label={t("onprem.userFilter")} hint={t("onprem.filterHint")} name="userFilter" dir="ltr" value={userFilter} onChange={(e) => setUserFilter(e.target.value)} />
      <FormField label={t("onprem.groupFilter")} hint={t("onprem.filterHint")} name="groupFilter" dir="ltr" value={groupFilter} onChange={(e) => setGroupFilter(e.target.value)} />
      <p className="field-hint">{t("onprem.accountNote")}</p>
      <div className="connections-row">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("settings.working")}>
          {submitLabel}
        </Button>
      </div>
    </form>
  );
}
