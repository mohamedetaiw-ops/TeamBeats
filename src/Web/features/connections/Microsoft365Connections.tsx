"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { M365_FEATURES, known, type FeatureView, type Microsoft365Connection } from "@/lib/integrations/types";
import { Messages, StatusBadge } from "./parts";
import { send } from "./request";

const RETURN_TO = "/admin/connections";

/**
 * Microsoft 365 connections in the Connections area (spec 006 US5, spec 017 T005): each connection with its status,
 * and adding one by its Microsoft tenant ID with the features to use. Features not in the package are shown but cannot
 * be chosen; the API checks every rule again.
 */
export function Microsoft365Connections({
  initial,
  catalogue,
  consent,
}: {
  initial: Microsoft365Connection[];
  catalogue: FeatureView[];
  consent: "granted" | "failed" | null;
}) {
  const t = useTranslations("connections");
  const format = useFormatter();
  const router = useRouter();
  const [tenantId, setTenantId] = useState("");
  const [name, setName] = useState("");
  const [features, setFeatures] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function create() {
    setBusy(true);
    setMessages([]);
    const body = { kind: "microsoft365", externalTenantId: tenantId.trim(), displayName: name.trim(), features: [...features] };
    const result = await send<Microsoft365Connection>("POST", "/api/v1/integrations/connections", body, RETURN_TO, t("failed"));
    if (result.ok) {
      router.push(`/admin/connections/microsoft365/${result.data.id}`);
      return;
    }
    setBusy(false);
    setMessages(result.messages);
  }

  return (
    <section className="connections-section" aria-labelledby="m365-heading">
      <h2 id="m365-heading">{t("m365.title")}</h2>
      <p>{t("m365.intro")}</p>
      {consent === "granted" ? <Messages ok messages={[t("m365.consentGranted")]} /> : null}
      {consent === "failed" ? <Messages messages={[t("m365.consentFailed")]} /> : null}
      {initial.length === 0 ? (
        <p className="notice">{t("m365.empty")}</p>
      ) : (
        <ul className="connections-list">
          {initial.map((c) => (
            <li key={c.id} className="connections-item">
              <h3>
                <Link href={`/admin/connections/microsoft365/${c.id}`}>
                  <bdi>{c.displayName}</bdi>
                </Link>
              </h3>
              <p className="connections-row">
                <StatusBadge status={c.status} />
                <span>{t("m365.featuresOn", { count: c.features.filter((f) => f.enabled).length })}</span>
                {c.lastSuccessAt ? <span>{t("list.lastSuccess", { at: format.dateTime(new Date(c.lastSuccessAt), { dateStyle: "medium", timeStyle: "short" }) })}</span> : null}
              </p>
            </li>
          ))}
        </ul>
      )}

      <h3>{t("m365.addTitle")}</h3>
      <form
        className="connections-form"
        aria-label={t("m365.addTitle")}
        onSubmit={(e) => {
          e.preventDefault();
          void create();
        }}
      >
        <FormField label={t("m365.tenantId")} hint={t("m365.tenantIdHint")} name="externalTenantId" dir="ltr" required value={tenantId} onChange={(e) => setTenantId(e.target.value)} />
        <FormField label={t("m365.name")} name="displayName" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
        <fieldset className="connections-fieldset">
          <legend>{t("m365.features")}</legend>
          {catalogue.map((f) => (
            <label key={f.key} className="connections-check">
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
          ))}
        </fieldset>
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("detail.working")}>
          {t("m365.add")}
        </Button>
      </form>
      <Messages messages={messages} />
    </section>
  );
}
