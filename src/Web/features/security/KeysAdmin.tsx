"use client";

import Link from "next/link";
import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { FactList } from "@/components/ui/Patterns";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { ResultAlert, useAdminAction, useKeyLabel, useWhen } from "@/features/admin/shared";
import type { TenantKey } from "@/lib/security/types";

const RETURN_TO = "/admin/security/keys";
const TONES = { active: "success", unreachable: "warning", revoked: "danger" } as const;
type Provider = "azure_key_vault" | "vault_transit";

/**
 * Customer-managed keys (spec 026 US8, FR-007): the key in the customer's key service that wraps the tenant's data key,
 * and what the last check found. Connecting or changing the key needs a fresh sign-in, and a second administrator when
 * four-eyes is on; the API checks every change again and refuses a key that cannot both wrap and unwrap.
 */
export function KeysAdmin({ initial }: { initial: TenantKey }) {
  const t = useTranslations("security.keys");
  const errorLabel = useKeyLabel("security.keys.errors");
  const when = useWhen();
  const action = useAdminAction(RETURN_TO);
  const [key, setKey] = useState(initial);
  const [provider, setProvider] = useState<Provider>(initial.provider ?? "azure_key_vault");
  const [keyUri, setKeyUri] = useState(initial.keyUri ?? "");
  const [secretRef, setSecretRef] = useState(initial.secretRef ?? "");
  const [directoryId, setDirectoryId] = useState(initial.directoryId ?? "");
  const [clientId, setClientId] = useState(initial.clientId ?? "");
  const azure = provider === "azure_key_vault";

  return (
    <div className="security">
      <ResultAlert result={action.result} />
      {key.status === "unreachable" ? (
        <Alert tone="warning" title={t("unreachableTitle")}>
          <p>{t("unreachableBody")}</p>
        </Alert>
      ) : null}
      {key.pendingChange?.status === "pending" ? (
        <p className="notice" data-testid="keys-pending-change">
          {t("waiting")} <Link href="/admin/security/changes">{t("openChanges")}</Link>
        </p>
      ) : null}
      <section className="security-section" aria-labelledby="keys-status-title" data-testid="keys-status">
        <h2 id="keys-status-title">{t("statusTitle")}</h2>
        {key.connected && key.status ? (
          <>
            <p>
              <Badge tone={TONES[key.status]}>{t(`statuses.${key.status}`)}</Badge>
            </p>
            <FactList
              facts={[
                { key: "provider", label: t("provider"), value: t(`providers.${key.provider}`) },
                { key: "keyUri", label: t("keyUri"), value: <code dir="ltr">{key.keyUri}</code> },
                { key: "version", label: t("version"), value: <code dir="ltr">{key.keyVersion}</code> },
                { key: "checked", label: t("checked"), value: when(key.checkedAt) },
                ...(key.rotatedAt ? [{ key: "rotated", label: t("rotated"), value: when(key.rotatedAt) }] : []),
                ...(key.status !== "active"
                  ? [{ key: "since", label: t("since"), value: t("sinceValue", { at: when(key.statusSince), error: errorLabel(key.lastError ?? "") }) }]
                  : []),
              ]}
            />
            {key.canManage ? (
              <div className="ui-control-row">
                <Button
                  variant="secondary"
                  busy={action.busy === "check"}
                  busyLabel={t("working")}
                  onClick={async () => {
                    const outcome = await action.run<TenantKey>("check", "POST", "/api/v1/security/keys:check", undefined, (d) => t(`checkedNow.${d.status ?? "active"}`));
                    if (outcome.ok) setKey(outcome.data);
                  }}
                >
                  {t("checkNow")}
                </Button>
              </div>
            ) : null}
          </>
        ) : (
          <p>{t("none")}</p>
        )}
      </section>
      {key.canManage ? (
        <form
          className="admin-form"
          onSubmit={async (e) => {
            e.preventDefault();
            const body = azure ? { provider, keyUri, secretRef, directoryId, clientId } : { provider, keyUri, secretRef };
            const outcome = await action.run<TenantKey>("save", "PUT", "/api/v1/security/keys", body, (d) =>
              d.pendingChange?.status === "pending" && d.keyUri !== keyUri.trim() ? t("held") : t("saved"),
            );
            if (outcome.ok) setKey(outcome.data);
          }}
        >
          <h2>{key.connected ? t("changeTitle") : t("connectTitle")}</h2>
          <p className="admin-meta">{key.connected ? t("changeIntro") : t("connectIntro")}</p>
          <RadioGroup
            legend={t("provider")}
            name="provider"
            value={provider}
            onChange={(e) => setProvider(e.target.value as Provider)}
            options={(["azure_key_vault", "vault_transit"] as const).map((v) => ({ value: v, label: t(`providers.${v}`) }))}
          />
          <div className="admin-fields">
            <FormField
              label={t("keyUri")}
              hint={t(azure ? "keyUriHintAzure" : "keyUriHintVault")}
              name="keyUri"
              type="url"
              dir="ltr"
              required
              maxLength={500}
              value={keyUri}
              onChange={(e) => setKeyUri(e.target.value)}
            />
            {azure ? (
              <>
                <FormField label={t("directoryId")} name="directoryId" dir="ltr" required maxLength={36} value={directoryId} onChange={(e) => setDirectoryId(e.target.value)} />
                <FormField label={t("clientId")} name="clientId" dir="ltr" required maxLength={36} value={clientId} onChange={(e) => setClientId(e.target.value)} />
              </>
            ) : null}
            <FormField
              label={t(azure ? "secretRefAzure" : "secretRefVault")}
              hint={t("secretRefHint")}
              name="secretRef"
              dir="ltr"
              required
              maxLength={200}
              value={secretRef}
              onChange={(e) => setSecretRef(e.target.value)}
            />
          </div>
          <div className="ui-control-row">
            <Button type="submit" variant="primary" busy={action.busy === "save"} busyLabel={t("working")}>
              {key.connected ? t("change") : t("connect")}
            </Button>
          </div>
        </form>
      ) : null}
    </div>
  );
}
