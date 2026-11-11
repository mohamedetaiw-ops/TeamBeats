"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField } from "@/components/ui/FormField";
import { GOOGLE_FEATURES, type GoogleSettings, type PackConnection } from "@/lib/integrations/types";

export type GoogleBody = {
  displayName: string;
  rateLimitPerMinute: number;
  settings: GoogleSettings;
};

/**
 * A Google Workspace connection (spec 017 T019): the domain, the administrator TeamBeats reads as, the service account
 * with domain-wide delegation and its key as a secret manager reference, the features in use, and for Google Drive the
 * shared drives TeamBeats may read. The API checks every value and the allowlist.
 */
export function GoogleSettingsForm({
  initial,
  submitLabel,
  busy,
  onSubmit,
}: {
  initial?: PackConnection;
  submitLabel: string;
  busy: boolean;
  onSubmit: (body: GoogleBody) => void;
}) {
  const t = useTranslations("connections.google");
  const ts = useTranslations("connections.settings");
  const s = initial?.settings as Partial<GoogleSettings> | undefined;
  const [name, setName] = useState(initial?.displayName ?? "");
  const [domain, setDomain] = useState(s?.domain ?? "");
  const [adminEmail, setAdminEmail] = useState(s?.adminEmail ?? "");
  const [account, setAccount] = useState(s?.serviceAccountEmail ?? "");
  const [keyRef, setKeyRef] = useState(s?.privateKeyRef ?? "");
  const [customerId, setCustomerId] = useState(s?.customerId && s.customerId !== "my_customer" ? s.customerId : "");
  const [features, setFeatures] = useState<string[]>(s?.features ?? ["directory"]);
  const [drives, setDrives] = useState((s?.sharedDrives ?? []).join("\n"));
  const [expiresOn, setExpiresOn] = useState(s?.expiresOn ?? "");
  const [rate, setRate] = useState(String(initial?.rateLimitPerMinute ?? 600));
  const drive = features.includes("drive");

  return (
    <form
      className="connections-form"
      aria-label={t("formLabel")}
      onSubmit={(e) => {
        e.preventDefault();
        onSubmit({
          displayName: name.trim(),
          rateLimitPerMinute: Number(rate),
          settings: {
            domain: domain.trim(),
            adminEmail: adminEmail.trim(),
            serviceAccountEmail: account.trim(),
            privateKeyRef: keyRef.trim(),
            customerId: customerId.trim() || null,
            features,
            sharedDrives: drive
              ? drives
                  .split(/[\s,]+/)
                  .map((d) => d.trim())
                  .filter(Boolean)
              : [],
            expiresOn: expiresOn || null,
          },
        });
      }}
    >
      <FormField label={ts("name")} name="displayName" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
      <FormField label={t("domain")} hint={t("domainHint")} name="domain" dir="ltr" required value={domain} onChange={(e) => setDomain(e.target.value)} />
      <FormField label={t("adminEmail")} hint={t("adminEmailHint")} name="adminEmail" type="email" dir="ltr" required value={adminEmail} onChange={(e) => setAdminEmail(e.target.value)} />
      <FormField label={t("serviceAccount")} hint={t("serviceAccountHint")} name="serviceAccountEmail" dir="ltr" required value={account} onChange={(e) => setAccount(e.target.value)} />
      <FormField label={t("keyRef")} hint={t("keyRefHint")} name="privateKeyRef" dir="ltr" required value={keyRef} onChange={(e) => setKeyRef(e.target.value)} />
      <FormField label={t("customerId")} hint={t("customerIdHint")} name="customerId" dir="ltr" value={customerId} onChange={(e) => setCustomerId(e.target.value)} />
      <fieldset className="connections-fieldset">
        <legend>{t("featuresTitle")}</legend>
        {GOOGLE_FEATURES.map((f) => (
          <Checkbox
            key={f}
            label={t(`features.${f}`)}
            checked={features.includes(f)}
            onChange={(e) => setFeatures((list) => (e.target.checked ? [...list.filter((x) => x !== f), f] : list.filter((x) => x !== f)))}
          />
        ))}
      </fieldset>
      {drive ? (
        <FormField label={t("sharedDrives")} hint={t("sharedDrivesHint")} name="sharedDrives" dir="ltr" required value={drives} onChange={(e) => setDrives(e.target.value)} />
      ) : null}
      <FormField label={ts("expiresOn")} hint={ts("expiresOnHint")} name="expiresOn" type="date" value={expiresOn} onChange={(e) => setExpiresOn(e.target.value)} />
      <FormField label={ts("rateLimit")} hint={ts("rateLimitHint")} name="rateLimitPerMinute" type="number" min={1} max={100000} value={rate} onChange={(e) => setRate(e.target.value)} />
      <div className="connections-row">
        <Button type="submit" variant="primary" busy={busy} busyLabel={ts("working")}>
          {submitLabel}
        </Button>
      </div>
    </form>
  );
}
