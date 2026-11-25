"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { ResultAlert, useAdminAction, useWhen } from "@/features/admin/shared";
import type { FourEyesSetting, PendingChange } from "@/lib/security/types";

const RETURN_TO = "/admin/security/changes";

/**
 * Four-eyes approval of critical settings (spec 026 FR-016): the tenant switch and the changes waiting for a second
 * administrator, with what each changes. Turning the switch off and approving need a fresh sign-in; the API decides who
 * may approve, reject or withdraw each change and checks it again.
 */
export function FourEyesAdmin({ setting, changes }: { setting: FourEyesSetting | null; changes: PendingChange[] }) {
  const t = useTranslations("security.changes");
  const when = useWhen();
  const action = useAdminAction(RETURN_TO);
  const [current, setCurrent] = useState(setting);
  const [rows, setRows] = useState(changes);

  async function reload() {
    const [s, c] = await Promise.all([
      fetch("/api/v1/security/four-eyes", { cache: "no-store" }).catch(() => null),
      fetch("/api/v1/security/changes", { cache: "no-store" }).catch(() => null),
    ]);
    if (s?.ok) setCurrent((await s.json()) as FourEyesSetting);
    if (c?.ok) setRows(((await c.json()) as { items: PendingChange[] }).items);
  }

  async function act(key: string, method: string, path: string, body: unknown, success: string) {
    const outcome = await action.run(key, method, path, body, success);
    if (outcome.ok) await reload();
  }

  const kind = (k: string) => (t.has(`kinds.${k.replaceAll(".", "_")}`) ? t(`kinds.${k.replaceAll(".", "_")}`) : k);

  return (
    <div className="security">
      <ResultAlert result={action.result} />
      {current ? (
        <section className="security-section" aria-labelledby="four-eyes-title" data-testid="four-eyes-setting">
          <h2 id="four-eyes-title">{t("switchTitle")}</h2>
          <p>{current.enabled ? t("on") : t("off")}</p>
          <p className="field-hint">{t("switchHint", { count: current.holders })}</p>
          {current.pending ? <p className="notice">{t("offWaiting")}</p> : null}
          <div className="ui-control-row">
            <Button
              variant={current.enabled ? "secondary" : "primary"}
              busy={action.busy === "switch"}
              busyLabel={t("working")}
              disabled={current.pending !== null}
              onClick={() =>
                void act("switch", "PUT", "/api/v1/security/four-eyes", { enabled: !current.enabled }, current.enabled ? t("offAsked") : t("turnedOn"))
              }
            >
              {current.enabled ? t("turnOff") : t("turnOn")}
            </Button>
          </div>
        </section>
      ) : null}
      <DataTable<PendingChange>
        caption={t("listTitle")}
        rows={rows}
        rowKey={(r) => r.id}
        testId="four-eyes-changes"
        emptyTitle={t("none")}
        emptyMessage={t("noneHint")}
        columns={[
          { key: "kind", header: t("kind"), rowHeader: true, cell: (r) => kind(r.kind) },
          { key: "fields", header: t("fields"), cell: (r) => r.fields.map((f) => (t.has(`fieldNames.${f}`) ? t(`fieldNames.${f}`) : f)).join(t("separator")) },
          { key: "requested", header: t("requested"), cell: (r) => when(r.requestedAt) },
          { key: "status", header: t("status"), cell: (r) => (r.status === "pending" ? t("waitsUntil", { date: when(r.expiresAt) }) : t(`statuses.${r.status}`)) },
          {
            key: "actions",
            header: t("actions"),
            align: "end",
            cell: (r) => (
              <div className="ui-table-actions">
                {r.canDecide ? (
                  <>
                    <Button
                      variant="primary"
                      size="sm"
                      busy={action.busy === `approve-${r.id}`}
                      busyLabel={t("working")}
                      onClick={() => void act(`approve-${r.id}`, "POST", `/api/v1/security/changes/${r.id}:approve`, undefined, t("approved"))}
                    >
                      {t("approve", { kind: kind(r.kind) })}
                    </Button>
                    <Button
                      size="sm"
                      busy={action.busy === `reject-${r.id}`}
                      busyLabel={t("working")}
                      onClick={() => void act(`reject-${r.id}`, "POST", `/api/v1/security/changes/${r.id}:reject`, {}, t("rejected"))}
                    >
                      {t("reject", { kind: kind(r.kind) })}
                    </Button>
                  </>
                ) : null}
                {r.canWithdraw ? (
                  <Button
                    size="sm"
                    busy={action.busy === `withdraw-${r.id}`}
                    busyLabel={t("working")}
                    onClick={() => void act(`withdraw-${r.id}`, "POST", `/api/v1/security/changes/${r.id}:withdraw`, undefined, t("withdrawn"))}
                  >
                    {t("withdraw", { kind: kind(r.kind) })}
                  </Button>
                ) : null}
              </div>
            ),
          },
        ]}
      />
    </div>
  );
}
