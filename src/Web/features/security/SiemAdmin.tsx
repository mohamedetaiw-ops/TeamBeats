"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, SelectField } from "@/components/ui/FormField";
import { NumberField } from "@/components/ui/Inputs";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { ResultAlert, useAdminAction, useKeyLabel, useWhen } from "@/features/admin/shared";
import type { SiemDestination, SiemPage, SiemTestResult } from "@/lib/security/types";

const RETURN_TO = "/admin/security/siem";

/** Where a destination stands: caught up, behind, failing or off. */
function state(d: SiemDestination): "off" | "failing" | "behind" | "current" {
  if (!d.enabled) return "off";
  if (d.failingSince) return "failing";
  return d.position < d.streamEnd ? "behind" : "current";
}

const TONES = { off: "neutral", failing: "danger", behind: "warning", current: "success" } as const;

/**
 * SIEM destinations (spec 026 US4, FR-014): audit and security events stream to HTTPS JSON signed with a secret from the
 * tenant's secret manager, or to syslog over TLS. Each destination keeps its place in the stream, so an outage holds
 * events back and they go out in order afterwards. Every change is checked again by the API, and needs step-up.
 */
export function SiemAdmin({ initial, canManage }: { initial: SiemDestination[]; canManage: boolean }) {
  const t = useTranslations("security.siem");
  const errorLabel = useKeyLabel("security.siem.errors");
  const when = useWhen();
  const action = useAdminAction(RETURN_TO);
  const [list, setList] = useState(initial);
  const [transport, setTransport] = useState<"https" | "syslog">("https");
  const [name, setName] = useState("");
  const [url, setUrl] = useState("");
  const [secretRef, setSecretRef] = useState("");
  const [host, setHost] = useState("");
  const [port, setPort] = useState(6514);
  const [historyDays, setHistoryDays] = useState("0");
  const [tested, setTested] = useState<{ id: string; result: SiemTestResult } | null>(null);
  const failing = list.filter((d) => state(d) === "failing");

  async function reload() {
    const response = await fetch("/api/v1/security/siem", { cache: "no-store" }).catch(() => null);
    if (response?.ok) setList(((await response.json()) as SiemPage).destinations);
  }

  return (
    <div className="security">
      {failing.length > 0 ? (
        <Alert tone="warning" title={t("failingTitle")}>
          <p>{t("failingBody")}</p>
        </Alert>
      ) : null}
      <ResultAlert result={action.result} />
      <DataTable
        caption={t("listTitle")}
        rows={list}
        rowKey={(d) => d.id}
        emptyTitle={t("none")}
        emptyMessage={t("noneHint")}
        columns={[
          { key: "name", header: t("name"), rowHeader: true, cell: (d) => <bdi>{d.name}</bdi> },
          { key: "target", header: t("target"), cell: (d) => <code dir="ltr">{d.target}</code> },
          { key: "transport", header: t("transport"), cell: (d) => t(`transports.${d.transport}`) },
          {
            key: "state",
            header: t("state"),
            cell: (d) => (
              <>
                <Badge tone={TONES[state(d)]}>{t(`states.${state(d)}`)}</Badge>
                {d.failingSince ? <p className="admin-meta">{t("failingSince", { at: when(d.failingSince), error: errorLabel(d.lastError ?? "") })}</p> : null}
                {state(d) === "behind" ? <p className="admin-meta">{t("waiting", { count: d.streamEnd - d.position })}</p> : null}
              </>
            ),
          },
          { key: "last", header: t("lastSuccess"), cell: (d) => when(d.lastSuccessAt) },
          {
            key: "actions",
            header: t("actions"),
            align: "end",
            cell: (d) =>
              canManage ? (
                <div className="ui-control-row">
                  <Button
                    size="sm"
                    variant="secondary"
                    busy={action.busy === "test-" + d.id}
                    busyLabel={t("working")}
                    onClick={async () => {
                      setTested(null);
                      const outcome = await action.run<SiemTestResult>("test-" + d.id, "POST", `/api/v1/security/siem/${d.id}:test`, undefined, (r) =>
                        r.delivered ? t("testDelivered") : t("testFailed", { error: errorLabel(r.errorClass ?? "") }),
                      );
                      if (outcome.ok) setTested({ id: d.id, result: outcome.data });
                    }}
                  >
                    {t("test")}
                  </Button>
                  <Button
                    size="sm"
                    variant="secondary"
                    busy={action.busy === "toggle-" + d.id}
                    busyLabel={t("working")}
                    onClick={async () => {
                      const outcome = await action.run("toggle-" + d.id, "POST", `/api/v1/security/siem/${d.id}:${d.enabled ? "disable" : "enable"}`, undefined,
                        d.enabled ? t("disabled") : t("enabled"));
                      if (outcome.ok) await reload();
                    }}
                  >
                    {d.enabled ? t("disable") : t("enable")}
                  </Button>
                  <ConfirmAction
                    label={t("remove")}
                    question={t("removeQuestion", { name: d.name })}
                    confirmLabel={t("remove")}
                    cancelLabel={t("cancel")}
                    onConfirm={async () => {
                      const outcome = await action.run("remove-" + d.id, "DELETE", `/api/v1/security/siem/${d.id}`, undefined, t("removed"));
                      if (outcome.ok) await reload();
                    }}
                  />
                </div>
              ) : null,
          },
        ]}
      />
      {tested && !tested.result.delivered ? <p className="admin-meta">{t("testHint")}</p> : null}
      {canManage && list.length < 2 ? (
        <form
          className="admin-form"
          onSubmit={async (e) => {
            e.preventDefault();
            const body =
              transport === "https"
                ? { name, transport, url, secretRef, historyDays: Number(historyDays) }
                : { name, transport, host, port, historyDays: Number(historyDays) };
            const outcome = await action.run("add", "POST", "/api/v1/security/siem", body, t("added"));
            if (outcome.ok) {
              setName("");
              await reload();
            }
          }}
        >
          <h2>{t("addTitle")}</h2>
          <p className="admin-meta">{t("addIntro")}</p>
          <RadioGroup
            legend={t("transport")}
            name="transport"
            value={transport}
            onChange={(e) => setTransport(e.target.value as "https" | "syslog")}
            options={(["https", "syslog"] as const).map((v) => ({ value: v, label: t(`transports.${v}`) }))}
          />
          <div className="admin-fields">
            <FormField label={t("name")} name="name" required maxLength={120} value={name} onChange={(e) => setName(e.target.value)} />
            {transport === "https" ? (
              <>
                <FormField label={t("url")} hint={t("urlHint")} name="url" type="url" dir="ltr" required maxLength={2000} value={url} onChange={(e) => setUrl(e.target.value)} />
                <FormField label={t("secretRef")} hint={t("secretRefHint")} name="secretRef" dir="ltr" required maxLength={200} value={secretRef} onChange={(e) => setSecretRef(e.target.value)} />
              </>
            ) : (
              <>
                <FormField label={t("host")} hint={t("hostHint")} name="host" dir="ltr" required maxLength={253} value={host} onChange={(e) => setHost(e.target.value)} />
                <NumberField label={t("port")} name="port" min={1} max={65535} value={port} onValueChange={(v) => setPort(v ?? 6514)} />
              </>
            )}
            <SelectField
              label={t("history")}
              hint={t("historyHint")}
              name="historyDays"
              value={historyDays}
              onChange={(e) => setHistoryDays(e.target.value)}
              options={["0", "1", "7"].map((v) => ({ value: v, label: t(`historyOptions.d${v}`) }))}
            />
          </div>
          <div className="ui-control-row">
            <Button type="submit" variant="primary" busy={action.busy === "add"} busyLabel={t("working")}>
              {t("add")}
            </Button>
          </div>
        </form>
      ) : null}
    </div>
  );
}
