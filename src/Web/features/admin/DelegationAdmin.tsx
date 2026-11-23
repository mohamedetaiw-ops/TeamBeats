"use client";

import { useMemo, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { DataTable } from "@/components/ui/DataTable";
import { SelectField } from "@/components/ui/FormField";
import { unitLabel, type OrgUnitView, type UserSummary } from "@/lib/admin/types";
import { ResultAlert, useAdminAction, useWhen } from "./shared";

export type Delegation = { orgUnitId: string; userId: string; areas: string[]; delegatedBy: string; delegatedAt: string };

/** The areas the API delegates (spec 033 R2 T019); the API refuses any other. */
const AREAS = ["pages", "brand", "navigation"] as const;

/**
 * Delegated experience administration for one organization (spec 033 R2 T019, FR-020): an administrator gives a person
 * the pages, brand or navigation of one organization and those below it. The API checks that the administrator may assign
 * roles there and holds what they give; this screen only shows what the API lists and sends what was chosen.
 */
export function DelegationAdmin({ initial, units, users }: { initial: Delegation[]; units: OrgUnitView[]; users: UserSummary[] }) {
  const t = useTranslations("admin.org.delegates");
  const locale = useLocale();
  const when = useWhen();
  const { busy, result, run } = useAdminAction("/admin/organization/delegates");
  const [rows, setRows] = useState(initial);
  const [unit, setUnit] = useState("");
  const [user, setUser] = useState("");
  const [areas, setAreas] = useState<string[]>([]);

  const unitById = useMemo(() => new Map(units.map((u) => [u.id, u])), [units]);
  const userById = useMemo(() => new Map(users.map((u) => [u.id, u])), [users]);
  const unitName = (id: string) => (unitById.get(id) ? unitLabel(unitById.get(id)!, locale) : t("unknown"));
  const userName = (id: string) => userById.get(id)?.displayName ?? t("unknown");
  const activeUnits = units.filter((u) => u.status === "active");
  const people = users.filter((u) => u.membershipStatus === "active");

  function pick(unitId: string, userId: string) {
    setUnit(unitId);
    setUser(userId);
    setAreas(rows.find((r) => r.orgUnitId === unitId && r.userId === userId)?.areas ?? []);
  }

  async function save() {
    const outcome = await run<Delegation>("save", "PUT", `/api/v1/experience/delegations/${unit}/${user}`, { areas }, () =>
      areas.length === 0 ? t("removed", { person: userName(user) }) : t("saved", { person: userName(user), organization: unitName(unit) }),
    );
    if (!outcome.ok) return;
    const rest = rows.filter((r) => !(r.orgUnitId === unit && r.userId === user));
    // No content means nothing is delegated any more.
    setRows(outcome.data && "areas" in outcome.data ? [...rest, outcome.data] : rest);
  }

  return (
    <div className="admin-screen">
      <ResultAlert result={result} />

      <section className="admin-panel" aria-labelledby="delegates-form-title">
        <div className="admin-panel-head">
          <h2 id="delegates-form-title">{t("formTitle")}</h2>
        </div>
        <p className="admin-meta">{t("intro")}</p>
        <div className="admin-fields">
          <SelectField
            label={t("organization")}
            name="organization"
            value={unit}
            onChange={(e) => pick(e.target.value, user)}
            options={[{ value: "", label: t("chooseOrganization") }, ...activeUnits.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]}
          />
          <SelectField
            label={t("person")}
            name="person"
            value={user}
            onChange={(e) => pick(unit, e.target.value)}
            options={[{ value: "", label: t("choosePerson") }, ...people.map((u) => ({ value: u.id, label: u.displayName }))]}
          />
        </div>
        <fieldset className="admin-fieldset">
          <legend>{t("areas")}</legend>
          <p className="admin-meta">{t("areasHint")}</p>
          <div className="admin-choices">
            {AREAS.map((a) => (
              <Checkbox
                key={a}
                card
                label={t(`area.${a}`)}
                hint={t(`areaHint.${a}`)}
                checked={areas.includes(a)}
                disabled={unit === "" || user === ""}
                onChange={(e) => setAreas(e.target.checked ? [...areas, a] : areas.filter((x) => x !== a))}
              />
            ))}
          </div>
        </fieldset>
        <div className="ui-control-row">
          <Button variant="primary" disabled={busy !== null || unit === "" || user === ""} onClick={() => void save()}>
            {t("save")}
          </Button>
        </div>
      </section>

      <section className="admin-panel" aria-labelledby="delegates-list-title">
        <div className="admin-panel-head">
          <h2 id="delegates-list-title">{t("listTitle")}</h2>
        </div>
        <DataTable
          caption={t("listTitle")}
          captionHidden
          rows={rows}
          rowKey={(r) => `${r.orgUnitId}-${r.userId}`}
          emptyTitle={t("emptyTitle")}
          emptyMessage={t("emptyMessage")}
          columns={[
            { key: "person", header: t("person"), rowHeader: true, cell: (r) => <bdi>{userName(r.userId)}</bdi>, sortValue: (r) => userName(r.userId) },
            { key: "organization", header: t("organization"), cell: (r) => <bdi>{unitName(r.orgUnitId)}</bdi>, sortValue: (r) => unitName(r.orgUnitId) },
            { key: "areas", header: t("areas"), cell: (r) => r.areas.map((a) => t(`area.${a}`)).join(t("separator")) },
            { key: "at", header: t("delegatedAt"), nowrap: true, priority: "low", cell: (r) => when(r.delegatedAt, "short") },
            {
              key: "edit",
              header: t("actions"),
              cell: (r) => (
                <Button variant="secondary" onClick={() => pick(r.orgUnitId, r.userId)} aria-label={t("editFor", { person: userName(r.userId) })}>
                  {t("edit")}
                </Button>
              ),
            },
          ]}
        />
      </section>
    </div>
  );
}
