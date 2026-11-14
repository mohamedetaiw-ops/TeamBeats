"use client";

import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, SelectField } from "@/components/ui/FormField";
import {
  M365_FEATURES,
  M365_PERMISSION_REASONS,
  M365_TEST_RESULTS,
  checkResult,
  known,
  type FeatureTestResult,
  type FeatureView,
  type Microsoft365Connection,
  type Microsoft365TestResult,
  type SharePointSite,
} from "@/lib/integrations/types";
import { Messages, StatusBadge, TestLine } from "./parts";
import { send } from "./request";

/** Mail and calendar options (spec 018): the API checks the ranges and changes the permissions the feature asks for. */
function FeatureOptions({ feature, busy, onSave }: { feature: FeatureView; busy: boolean; onSave: (body: Record<string, unknown>) => void }) {
  const t = useTranslations("connections");
  const o = feature.options ?? {};
  const [previewLine, setPreviewLine] = useState(o.previewLine ?? false);
  const [maxMessages, setMaxMessages] = useState(String(o.maxMessages ?? 10));
  const [maxDays, setMaxDays] = useState(String(o.maxDays ?? 14));
  if (feature.key !== "mail" && feature.key !== "calendar") return null;
  return (
    <form
      className="connections-form"
      aria-label={t("m365.optionsFor", { feature: t(`m365.feature.${feature.key === "mail" ? "mail" : "calendar"}`) })}
      onSubmit={(e) => {
        e.preventDefault();
        onSave(feature.key === "mail" ? { previewLine, maxMessages: Number(maxMessages) } : { maxDays: Number(maxDays) });
      }}
    >
      {feature.key === "mail" ? (
        <>
          <label className="connections-check">
            <input type="checkbox" checked={previewLine} onChange={(e) => setPreviewLine(e.target.checked)} />
            {t("m365.previewLine")}
          </label>
          <FormField label={t("m365.maxMessages")} hint={t("m365.maxMessagesHint")} name="maxMessages" type="number" min={1} max={10} value={maxMessages} onChange={(e) => setMaxMessages(e.target.value)} />
        </>
      ) : (
        <FormField label={t("m365.maxDays")} hint={t("m365.maxDaysHint")} name="maxDays" type="number" min={1} max={14} value={maxDays} onChange={(e) => setMaxDays(e.target.value)} />
      )}
      <div className="connections-row">
        <Button type="submit" busy={busy} busyLabel={t("detail.working")}>
          {t("m365.saveOptions")}
        </Button>
      </div>
    </form>
  );
}

type SensitivityLabel = { labelId: string; name: string | null; files: number; classification: string | null };
const CLASSIFICATIONS = ["public", "internal", "confidential", "restricted"] as const;

/**
 * Microsoft Purview labels and the TeamBeats classification each maps to (spec 011 FR-032). Labels appear once synced
 * files carry them; an unmapped label counts as confidential, which the API applies, so this only lists and saves.
 */
function SharePointLabels({ connectionId, returnTo }: { connectionId: string; returnTo: string }) {
  const t = useTranslations("connections");
  const [labels, setLabels] = useState<SensitivityLabel[] | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<{ ok: boolean; list: string[] }>({ ok: true, list: [] });
  const path = `/api/v1/documents/sensitivity-labels/${encodeURIComponent(connectionId)}`;

  useEffect(() => {
    let live = true;
    void send<SensitivityLabel[]>("GET", path, undefined, returnTo, t("detail.failed")).then((result) => {
      if (!live) return;
      if (result.ok) setLabels(result.data);
      else setMessages({ ok: false, list: result.messages });
    });
    return () => {
      live = false;
    };
  }, [path, returnTo, t]);

  async function save(label: SensitivityLabel, value: string) {
    setBusy(label.labelId);
    setMessages({ ok: true, list: [] });
    const result = await send<SensitivityLabel>("PUT", `${path}/${encodeURIComponent(label.labelId)}`, { classification: value || null }, returnTo,
      t("detail.failed"));
    if (result.ok) {
      setLabels((all) => (all ?? []).map((l) => (l.labelId === label.labelId ? result.data : l)));
      setMessages({ ok: true, list: [t("m365.labels.saved")] });
    } else {
      setMessages({ ok: false, list: result.messages });
    }
    setBusy(null);
  }

  return (
    <section className="connections-item" aria-labelledby="sharepoint-labels-heading">
      <h4 id="sharepoint-labels-heading">{t("m365.labels.title")}</h4>
      <p>{t("m365.labels.intro")}</p>
      {labels && labels.length === 0 ? <p>{t("m365.labels.empty")}</p> : null}
      {labels && labels.length > 0 ? (
        <ul className="connections-list">
          {labels.map((label) => (
            <li key={label.labelId} className="connections-row">
              <SelectField
                name={`label-${label.labelId}`}
                label={label.name ?? label.labelId}
                hint={t("m365.labels.files", { count: label.files })}
                value={label.classification ?? ""}
                disabled={busy === label.labelId}
                onChange={(e) => void save(label, e.target.value)}
                options={[
                  { value: "", label: t("m365.labels.unmapped") },
                  ...CLASSIFICATIONS.map((c) => ({ value: c, label: t(`m365.labels.classification.${c}`) })),
                ]}
              />
            </li>
          ))}
        </ul>
      ) : null}
      <Messages ok={messages.ok} messages={messages.list} />
    </section>
  );
}

/**
 * The allowed sites of SharePoint documents (spec 011 FR-025). The API resolves each address with the application's own
 * access and refuses a site the customer has not granted, so this only lists, adds and removes.
 */
function SharePointSites({ base, returnTo }: { base: string; returnTo: string }) {
  const t = useTranslations("connections");
  const [sites, setSites] = useState<SharePointSite[] | null>(null);
  const [url, setUrl] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<{ ok: boolean; list: string[] }>({ ok: true, list: [] });
  const path = `${base}/sharepoint/sites`;

  async function load() {
    const result = await send<SharePointSite[]>("GET", path, undefined, returnTo, t("detail.failed"));
    if (result.ok) setSites(result.data);
    else setMessages({ ok: false, list: result.messages });
  }

  useEffect(() => {
    let live = true;
    void send<SharePointSite[]>("GET", path, undefined, returnTo, t("detail.failed")).then((result) => {
      if (!live) return;
      if (result.ok) setSites(result.data);
      else setMessages({ ok: false, list: result.messages });
    });
    return () => {
      live = false;
    };
  }, [path, returnTo, t]);

  async function act(key: string, method: string, target: string, body: unknown, note: string) {
    setBusy(key);
    setMessages({ ok: true, list: [] });
    const result = await send<unknown>(method, target, body, returnTo, t("detail.failed"));
    if (result.ok) {
      await load();
      setMessages({ ok: true, list: [note] });
      if (key === "add") setUrl("");
    } else {
      setMessages({ ok: false, list: result.messages });
    }
    setBusy(null);
  }

  return (
    <section className="connections-item" aria-labelledby="sharepoint-sites-heading">
      <h4 id="sharepoint-sites-heading">{t("m365.sites.title")}</h4>
      <p>{t("m365.sites.intro")}</p>
      {sites && sites.length === 0 ? <p>{t("m365.sites.empty")}</p> : null}
      {sites && sites.length > 0 ? (
        <ul className="connections-list">
          {sites.map((site) => (
            <li key={site.siteId} className="connections-row">
              <a href={site.webUrl} target="_blank" rel="noreferrer">
                {site.name}
              </a>
              <Button
                busy={busy === `remove:${site.siteId}`}
                busyLabel={t("detail.working")}
                onClick={() => void act(`remove:${site.siteId}`, "DELETE", `${path}/${encodeURIComponent(site.siteId)}`, undefined, t("m365.sites.removed"))}
              >
                {t("m365.sites.remove", { name: site.name })}
              </Button>
            </li>
          ))}
        </ul>
      ) : null}
      <form
        className="connections-form"
        onSubmit={(e) => {
          e.preventDefault();
          void act("add", "POST", path, { url }, t("m365.sites.added"));
        }}
      >
        <FormField label={t("m365.sites.url")} hint={t("m365.sites.urlHint")} name="siteUrl" type="url" dir="ltr" value={url} onChange={(e) => setUrl(e.target.value)} />
        <div className="connections-row">
          <Button type="submit" busy={busy === "add"} busyLabel={t("detail.working")}>
            {t("m365.sites.add")}
          </Button>
        </div>
      </form>
      <Messages messages={messages.list} ok={messages.ok} />
    </section>
  );
}

/**
 * One Microsoft 365 connection (spec 006 US5, spec 018): status, each feature with the Microsoft permissions it needs
 * and why, admin consent, feature options, the test per feature, turning the connection on and off, and delete. The
 * API decides every rule, including which features the package includes.
 */
export function Microsoft365Detail({ initial }: { initial: Microsoft365Connection }) {
  const t = useTranslations("connections");
  const format = useFormatter();
  const router = useRouter();
  const returnTo = `/admin/connections/microsoft365/${initial.id}`;
  const base = `/api/v1/integrations/connections/${initial.id}`;
  const [view, setView] = useState(initial);
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<{ ok: boolean; list: string[] }>({ ok: true, list: [] });
  const [tests, setTests] = useState<Record<string, FeatureTestResult>>({});

  async function reload() {
    const result = await send<Microsoft365Connection>("GET", base, undefined, returnTo, t("detail.failed"));
    if (result.ok) setView(result.data);
  }

  async function act<T>(key: string, method: string, path: string, body: unknown, done: (data: T) => string | null) {
    setBusy(key);
    setMessages({ ok: true, list: [] });
    const result = await send<T>(method, path, body, returnTo, t("detail.failed"));
    if (result.ok) {
      const note = done(result.data);
      await reload();
      setMessages({ ok: true, list: note ? [note] : [] });
    } else {
      setMessages({ ok: false, list: result.messages });
    }
    setBusy(null);
  }

  const featureName = (key: string) => (known(M365_FEATURES, key) ? t(`m365.feature.${key}`) : key);

  return (
    <div className="connections">
      <section className="connections-section" aria-labelledby="state-heading">
        <h2 id="state-heading">{t("detail.state")}</h2>
        <dl className="connections-facts">
          <dt>{t("detail.status")}</dt>
          <dd>
            <StatusBadge status={view.status} />
          </dd>
          <dt>{t("m365.tenantId")}</dt>
          <dd>
            <bdi dir="ltr">{view.externalTenantId}</bdi>
          </dd>
          <dt>{t("detail.lastSuccess")}</dt>
          <dd>{view.lastSuccessAt ? format.dateTime(new Date(view.lastSuccessAt), { dateStyle: "medium", timeStyle: "short" }) : t("detail.never")}</dd>
        </dl>
        <div className="connections-row">
          <Button
            busy={busy === "test"}
            busyLabel={t("detail.working")}
            onClick={() =>
              void act<Microsoft365TestResult>("test", "POST", `${base}:test`, undefined, (r) => {
                setTests(Object.fromEntries(r.features.map((f) => [f.feature, f])));
                return t(r.features.every((f) => f.ok) ? "detail.testPassed" : "detail.testFailed");
              })
            }
          >
            {t("detail.test")}
          </Button>
          {view.enabled ? (
            <Button busy={busy === "toggle"} busyLabel={t("detail.working")} onClick={() => void act<null>("toggle", "POST", `${base}:disable`, undefined, () => t("detail.disabled"))}>
              {t("detail.disable")}
            </Button>
          ) : (
            <Button variant="primary" busy={busy === "toggle"} busyLabel={t("detail.working")} onClick={() => void act<null>("toggle", "POST", `${base}:enable`, undefined, () => t("detail.enabled"))}>
              {t("detail.enable")}
            </Button>
          )}
        </div>
        <Messages messages={messages.list} ok={messages.ok} />
      </section>

      <section className="connections-section" aria-labelledby="features-heading">
        <h2 id="features-heading">{t("m365.features")}</h2>
        <p>{t("m365.featuresIntro")}</p>
        <ul className="connections-list">
          {view.features.map((f) => (
            <li key={f.key} className="connections-item">
              <h3>{featureName(f.key)}</h3>
              <p className="connections-row">
                <span>{t(f.enabled ? "m365.featureOn" : "m365.featureOff")}</span>
                <span>{t(f.consentStatus === "granted" ? "m365.consent.granted" : "m365.consent.pending")}</span>
                {f.inPackage ? null : <span>{t("catalogue.notInPackage")}</span>}
              </p>
              <h4>{t("m365.permissions.title")}</h4>
              <ul>
                {f.permissions.map((p) => {
                  const reason = checkResult(p.reasonKey);
                  return (
                    <li key={p.name}>
                      <bdi dir="ltr">{p.name}</bdi> {t("m365.permissionKind", { type: t(p.type.toLowerCase() === "application" ? "m365.application" : "m365.delegated") })}{" "}
                      {known(M365_PERMISSION_REASONS, reason) ? t(`m365.permissions.${reason}`) : reason}
                    </li>
                  );
                })}
              </ul>
              {f.key in tests ? <TestLine result={tests[f.key]!} /> : null}
              <div className="connections-row">
                {f.inPackage ? (
                  f.enabled ? (
                    <Button
                      busy={busy === `feature:${f.key}`}
                      busyLabel={t("detail.working")}
                      aria-label={t("m365.turnOffFeature", { feature: featureName(f.key) })}
                      onClick={() => void act<null>(`feature:${f.key}`, "POST", `${base}/features/${f.key}:disable`, undefined, () => null)}
                    >
                      {t("detail.disable")}
                    </Button>
                  ) : (
                    <Button
                      busy={busy === `feature:${f.key}`}
                      busyLabel={t("detail.working")}
                      aria-label={t("m365.turnOnFeature", { feature: featureName(f.key) })}
                      onClick={() => void act<null>(`feature:${f.key}`, "POST", `${base}/features/${f.key}:enable`, undefined, () => null)}
                    >
                      {t("detail.enable")}
                    </Button>
                  )
                ) : null}
                {f.enabled && f.consentStatus !== "granted" ? (
                  <a className="button button-primary" href={`${base}/features/${f.key}/consent`}>
                    {t("m365.grantConsent", { feature: featureName(f.key) })}
                  </a>
                ) : null}
              </div>
              {f.key === "sharepoint_documents" && f.enabled && f.consentStatus === "granted" ? (
                <>
                  <SharePointSites base={base} returnTo={returnTo} />
                  <SharePointLabels connectionId={initial.id} returnTo={returnTo} />
                </>
              ) : null}
              {f.enabled ? (
                <FeatureOptions
                  key={JSON.stringify(f.options ?? null)}
                  feature={f}
                  busy={busy === `options:${f.key}`}
                  onSave={(body) => void act<Microsoft365Connection>(`options:${f.key}`, "PUT", `${base}/features/${f.key}/options`, body, () => t("detail.saved"))}
                />
              ) : null}
            </li>
          ))}
        </ul>
      </section>

      <section className="connections-section" aria-labelledby="delete-heading">
        <h2 id="delete-heading">{t("detail.deleteTitle")}</h2>
        <p>{t("m365.deleteIntro")}</p>
        <ConfirmAction
          label={t("detail.delete")}
          question={t("m365.deleteQuestion", { name: view.displayName })}
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
