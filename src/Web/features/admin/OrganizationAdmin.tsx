"use client";

import { useEffect, useId, useMemo, useState, type ReactNode } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable } from "@/components/ui/DataTable";
import { Dialog } from "@/components/ui/Dialog";
import { FormField, SelectField } from "@/components/ui/FormField";
import { FactList } from "@/components/ui/Patterns";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { StateView } from "@/components/ui/StateView";
import { TreeView, type TreeNode } from "@/components/ui/TreeView";
import { PersonPicker } from "@/features/services/PersonPicker";
import { MEMBERSHIP_TYPES, unitLabel, type OrgMembership, type OrgUnitView, type UnitType } from "@/lib/admin/types";
import { ResultAlert, useAdminAction, usePeopleNames, useWhen } from "./shared";

const RETURN = "/admin/organization";
const NAME_LOCALES = ["en", "ar"] as const;

type Form =
  | { kind: "create"; parentId: string | null }
  | { kind: "rename" }
  | { kind: "move" }
  | { kind: "deactivate" }
  | { kind: "member" }
  | { kind: "type" }
  | null;

/**
 * Organization (spec 002): the unit tree, one unit's details and members, and the changes the API allows on it: add a
 * unit under it, rename, move, deactivate, add and end memberships, and custom unit types. Units that the directory
 * owns (spec 002 US5) say so and offer no structural change; the API refuses those anyway.
 */
export function OrganizationAdmin({ initialUnits, types: initialTypes }: { initialUnits: OrgUnitView[]; types: UnitType[] }) {
  const t = useTranslations("admin.org");
  const locale = useLocale();
  const when = useWhen();
  const { busy, result, run, setResult } = useAdminAction(RETURN);
  const [units, setUnits] = useState(initialUnits);
  const [types, setTypes] = useState(initialTypes);
  const [selected, setSelected] = useState<string | null>(initialUnits.find((u) => !u.parentId)?.id ?? initialUnits[0]?.id ?? null);
  const [loaded, setLoaded] = useState<{ unit: string; list: OrgMembership[] } | null>(null);
  const [form, setForm] = useState<Form>(null);
  const [showInactive, setShowInactive] = useState(false);

  const unit = units.find((u) => u.id === selected) ?? null;
  const members = loaded && loaded.unit === selected ? loaded.list : null;
  const byId = useMemo(() => new Map(units.map((u) => [u.id, u])), [units]);
  const name = (id: string | null | undefined) => (id && byId.get(id) ? unitLabel(byId.get(id)!, locale) : "");
  const typeName = (key: string) => {
    const custom = types.find((x) => x.key === key)?.names;
    if (custom) return custom[locale] ?? custom.en ?? key;
    return t.has(`types.${key}`) ? t(`types.${key}`) : key;
  };
  const person = usePeopleNames(members?.map((m) => m.userId) ?? []);

  const tree = useMemo(() => {
    const visible = units.filter((u) => showInactive || u.status === "active");
    const children = new Map<string | null, OrgUnitView[]>();
    const ids = new Set(visible.map((u) => u.id));
    for (const u of visible) {
      const parent = u.parentId && ids.has(u.parentId) ? u.parentId : null;
      children.set(parent, [...(children.get(parent) ?? []), u]);
    }
    const build = (parent: string | null): TreeNode[] =>
      (children.get(parent) ?? [])
        .sort((a, b) => unitLabel(a, locale).localeCompare(unitLabel(b, locale)))
        .map((u) => ({
          key: u.id,
          label: u.status === "active" ? unitLabel(u, locale) : t("inactiveName", { name: unitLabel(u, locale) }),
          icon: "folder" as const,
          children: children.has(u.id) ? build(u.id) : undefined,
        }));
    return build(null);
  }, [units, showInactive, locale, t]);

  async function reloadUnits() {
    const response = await fetch("/api/v1/org/units", { cache: "no-store" });
    if (response.ok) setUnits((await response.json()) as OrgUnitView[]);
  }

  async function reloadMembers(id: string) {
    const response = await fetch(`/api/v1/org/units/${id}/memberships`, { cache: "no-store" });
    setLoaded({ unit: id, list: response.ok ? ((await response.json()) as OrgMembership[]) : [] });
  }

  useEffect(() => {
    if (!selected) return;
    let cancelled = false;
    void (async () => {
      const response = await fetch(`/api/v1/org/units/${selected}/memberships`, { cache: "no-store" });
      const list = response.ok ? ((await response.json()) as OrgMembership[]) : [];
      if (!cancelled) setLoaded({ unit: selected, list });
    })();
    return () => {
      cancelled = true;
    };
  }, [selected]);

  // The tree opens the branches above the chosen unit, also after a new unit is added under a closed branch.
  const ancestors: string[] = [];
  for (let at = unit?.parentId ? byId.get(unit.parentId) : undefined; at; at = at.parentId ? byId.get(at.parentId) : undefined) {
    ancestors.push(at.id);
  }

  const directory = unit?.owner === "directory";
  const active = unit?.status === "active";

  return (
    <div className="admin-screen">
      <ResultAlert result={result} />
      <div className="admin-split">
        <section className="admin-panel admin-tree" aria-labelledby="org-tree-title">
          <div className="admin-panel-head">
            <h2 id="org-tree-title">{t("tree.title")}</h2>
            <Button size="sm" icon="plus" onClick={() => setForm({ kind: "create", parentId: null })}>
              {t("tree.addTop")}
            </Button>
          </div>
          <Checkbox name="showInactive" label={t("tree.showInactive")} checked={showInactive} onChange={(e) => setShowInactive(e.target.checked)} />
          {tree.length === 0 ? (
            <StateView kind="empty" title={t("tree.empty")} message={t("tree.emptyHint")} />
          ) : (
            <TreeView
              key={`${units.length}-${showInactive}`}
              label={t("tree.title")}
              nodes={tree}
              selected={selected}
              onSelect={setSelected}
              defaultExpanded={[...units.filter((u) => u.level <= 1).map((u) => u.id), ...ancestors]}
            />
          )}
        </section>

        <section className="admin-panel" aria-labelledby="org-unit-title">
          {unit ? (
            <>
              <div className="admin-panel-head">
                <h2 id="org-unit-title">
                  <bdi>{unitLabel(unit, locale)}</bdi>
                </h2>
                <div className="admin-badges">
                  <Badge tone={active ? "success" : "neutral"}>{t(`status.${active ? "active" : "inactive"}`)}</Badge>
                  {directory ? <Badge tone="info" icon="lock">{t("directoryOwned")}</Badge> : null}
                </div>
              </div>
              <FactList
                facts={[
                  { key: "code", label: t("unit.code"), value: <bdi dir="ltr">{unit.code}</bdi> },
                  { key: "type", label: t("unit.type"), value: typeName(unit.typeKey) },
                  { key: "parent", label: t("unit.parent"), value: unit.parentId ? <bdi>{name(unit.parentId)}</bdi> : t("unit.top") },
                  { key: "level", label: t("unit.level"), value: unit.level },
                  ...NAME_LOCALES.map((l) => ({ key: `name-${l}`, label: t(`unit.name_${l}`), value: unit.names[l] ? <bdi>{unit.names[l]}</bdi> : t("unit.noName") })),
                ]}
              />
              {directory ? <p className="admin-meta">{t("directoryHint")}</p> : null}
              {active ? (
                <div className="admin-row admin-actions">
                  <Button size="sm" icon="plus" onClick={() => setForm({ kind: "create", parentId: unit.id })}>
                    {t("actions.addChild")}
                  </Button>
                  <Button size="sm" icon="edit" onClick={() => setForm({ kind: "rename" })}>
                    {t("actions.rename")}
                  </Button>
                  {!directory ? (
                    <>
                      <Button size="sm" onClick={() => setForm({ kind: "move" })}>
                        {t("actions.move")}
                      </Button>
                      <Button size="sm" variant="destructive" onClick={() => setForm({ kind: "deactivate" })}>
                        {t("actions.deactivate")}
                      </Button>
                    </>
                  ) : null}
                </div>
              ) : null}

              <div className="admin-panel-head">
                <h3>{t("members.title")}</h3>
                {active ? (
                  <Button size="sm" icon="plus" onClick={() => setForm({ kind: "member" })}>
                    {t("members.add")}
                  </Button>
                ) : null}
              </div>
              {members === null ? (
                <StateView kind="loading" />
              ) : (
                <DataTable
                  caption={t("members.caption", { name: unitLabel(unit, locale) })}
                  rows={members}
                  rowKey={(m) => m.id}
                  emptyTitle={t("members.empty")}
                  density="compact"
                  columns={[
                    { key: "person", header: t("members.person"), rowHeader: true, cell: (m) => <bdi>{person(m.userId)}</bdi>, sortValue: (m) => person(m.userId) },
                    { key: "type", header: t("members.type"), cell: (m) => t(`membershipTypes.${m.type}`) },
                    { key: "from", header: t("members.from"), cell: (m) => when(m.validFrom, "short"), sortValue: (m) => m.validFrom },
                    {
                      key: "end",
                      header: t("members.actions"),
                      cell: (m) => (
                        <ConfirmAction
                          label={t("members.end")}
                          question={t("members.endQuestion", { name: person(m.userId) })}
                          confirmLabel={t("members.endYes")}
                          cancelLabel={t("cancel")}
                          onConfirm={async () => {
                            const outcome = await run("end", "POST", `/api/v1/org/memberships/${m.id}:end`, undefined, t("members.ended"));
                            if (outcome.ok) await reloadMembers(unit.id);
                          }}
                        />
                      ),
                    },
                  ]}
                />
              )}
            </>
          ) : (
            <StateView kind="empty" title={t("unit.none")} message={t("unit.noneHint")} />
          )}
        </section>
      </div>

      <section className="admin-panel" aria-labelledby="org-types-title">
        <div className="admin-panel-head">
          <h2 id="org-types-title">{t("typesTitle")}</h2>
          <Button size="sm" icon="plus" onClick={() => setForm({ kind: "type" })}>
            {t("typeAdd")}
          </Button>
        </div>
        <ul className="admin-chips">
          {types.map((x) => (
            <li key={x.key}>
              <Badge tone={x.builtIn ? "neutral" : "accent"}>{typeName(x.key)}</Badge>
            </li>
          ))}
        </ul>
      </section>

      {form?.kind === "create" ? (
        <UnitForm
          title={form.parentId ? t("form.createUnder", { name: name(form.parentId) }) : t("form.createTop")}
          types={types}
          typeName={typeName}
          busy={busy === "create"}
          onClose={() => setForm(null)}
          onSubmit={async (values) => {
            const outcome = await run<{ unit: OrgUnitView; warnings: string[] }>(
              "create",
              "POST",
              "/api/v1/org/units",
              { parentId: form.parentId, typeKey: values.typeKey, code: values.code, names: values.names },
              (data) => (data.warnings.length > 0 ? t("form.createdWithWarning") : t("form.created")),
            );
            if (outcome.ok) {
              setForm(null);
              // Chosen first, so the tree opens the new unit's branch when it redraws with the new unit.
              setSelected(outcome.data.unit.id);
              await reloadUnits();
            }
          }}
        />
      ) : null}

      {form?.kind === "rename" && unit ? (
        <UnitForm
          title={t("form.rename", { name: unitLabel(unit, locale) })}
          types={types}
          typeName={typeName}
          initial={unit}
          lockType={directory}
          busy={busy === "rename"}
          onClose={() => setForm(null)}
          onSubmit={async (values) => {
            const names = Object.fromEntries(Object.entries(values.names).filter(([l, v]) => v !== (unit.names[l] ?? "")));
            const outcome = await run("rename", "PATCH", `/api/v1/org/units/${unit.id}`, { names, typeKey: values.typeKey === unit.typeKey ? null : values.typeKey }, t("form.renamed"));
            if (outcome.ok) {
              setForm(null);
              await reloadUnits();
            }
          }}
        />
      ) : null}

      {form?.kind === "move" && unit ? (
        <MoveForm
          unit={unit}
          units={units}
          name={(u) => unitLabel(u, locale)}
          busy={busy === "move"}
          onClose={() => setForm(null)}
          onSubmit={async (parentId) => {
            const outcome = await run<{ affectedUnits: number }>("move", "POST", `/api/v1/org/units/${unit.id}:move`, { newParentId: parentId }, (d) =>
              t("form.moved", { count: d.affectedUnits }),
            );
            if (outcome.ok) {
              setForm(null);
              await reloadUnits();
            }
          }}
        />
      ) : null}

      {form?.kind === "deactivate" && unit ? (
        <DeactivateForm
          unit={unit}
          units={units}
          hasChildren={units.some((u) => u.parentId === unit.id && u.status === "active")}
          hasMembers={(members?.length ?? 0) > 0}
          name={(u) => unitLabel(u, locale)}
          busy={busy === "deactivate"}
          onClose={() => setForm(null)}
          onSubmit={async (body) => {
            const outcome = await run("deactivate", "POST", `/api/v1/org/units/${unit.id}:deactivate`, body, t("form.deactivated"));
            if (outcome.ok) {
              setForm(null);
              await reloadUnits();
              await reloadMembers(unit.id);
            }
          }}
        />
      ) : null}

      {form?.kind === "member" && unit ? (
        <MemberForm
          unitName={unitLabel(unit, locale)}
          busy={busy === "member"}
          onClose={() => setForm(null)}
          onSubmit={async (userId, type) => {
            const outcome = await run("member", "POST", `/api/v1/org/units/${unit.id}/memberships`, { userId, type }, t("members.added"));
            if (outcome.ok) {
              setForm(null);
              await reloadMembers(unit.id);
            }
          }}
        />
      ) : null}

      {form?.kind === "type" ? (
        <TypeForm
          busy={busy === "type"}
          onClose={() => {
            setForm(null);
            setResult(null);
          }}
          onSubmit={async (key, names) => {
            const outcome = await run("type", "POST", "/api/v1/org/unit-types", { key, names }, t("typeAdded"));
            if (outcome.ok) {
              setForm(null);
              setTypes((before) => [...before, { key, builtIn: false, names }]);
            }
          }}
        />
      ) : null}
    </div>
  );
}

function FormDialog({ title, busy, onClose, onSubmit, children, submitLabel, danger = false }: {
  title: string;
  busy: boolean;
  onClose: () => void;
  onSubmit: () => void;
  children: ReactNode;
  submitLabel: string;
  danger?: boolean;
}) {
  const t = useTranslations("admin.org");
  const formId = useId();
  return (
    <Dialog
      open
      onClose={onClose}
      title={title}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            {t("cancel")}
          </Button>
          <Button type="submit" form={formId} variant={danger ? "destructive" : "primary"} busy={busy} busyLabel={t("working")}>
            {submitLabel}
          </Button>
        </>
      }
    >
      <form
        id={formId}
        className="admin-form"
        onSubmit={(e) => {
          e.preventDefault();
          onSubmit();
        }}
      >
        {children}
      </form>
    </Dialog>
  );
}

function UnitForm({ title, types, typeName, initial, lockType = false, busy, onClose, onSubmit }: {
  title: string;
  types: UnitType[];
  typeName: (key: string) => string;
  initial?: OrgUnitView;
  lockType?: boolean;
  busy: boolean;
  onClose: () => void;
  onSubmit: (values: { typeKey: string; code: string; names: Record<string, string> }) => void;
}) {
  const t = useTranslations("admin.org");
  const [typeKey, setTypeKey] = useState(initial?.typeKey ?? "department");
  const [code, setCode] = useState(initial?.code ?? "");
  const [names, setNames] = useState<Record<string, string>>(Object.fromEntries(NAME_LOCALES.map((l) => [l, initial?.names[l] ?? ""])));
  return (
    <FormDialog title={title} busy={busy} onClose={onClose} submitLabel={initial ? t("form.save") : t("form.create")} onSubmit={() => onSubmit({ typeKey, code: code.trim(), names: Object.fromEntries(Object.entries(names).map(([l, v]) => [l, v.trim()]).filter(([, v]) => v)) })}>
      {NAME_LOCALES.map((l) => (
        <FormField key={l} label={t(`unit.name_${l}`)} name={`name-${l}`} dir={l === "ar" ? "rtl" : "ltr"} value={names[l] ?? ""} onChange={(e) => setNames({ ...names, [l]: e.target.value })} />
      ))}
      {initial ? null : <FormField label={t("unit.code")} hint={t("form.codeHint")} name="code" dir="ltr" required value={code} onChange={(e) => setCode(e.target.value)} />}
      <SelectField label={t("unit.type")} name="typeKey" value={typeKey} disabled={lockType} onChange={(e) => setTypeKey(e.target.value)} options={types.map((x) => ({ value: x.key, label: typeName(x.key) }))} />
    </FormDialog>
  );
}

function MoveForm({ unit, units, name, busy, onClose, onSubmit }: {
  unit: OrgUnitView;
  units: OrgUnitView[];
  name: (u: OrgUnitView) => string;
  busy: boolean;
  onClose: () => void;
  onSubmit: (parentId: string | null) => void;
}) {
  const t = useTranslations("admin.org");
  // A unit cannot move under itself or its own descendants; the API checks this too.
  const below = new Set<string>([unit.id]);
  let grew = true;
  while (grew) {
    grew = false;
    for (const u of units) {
      if (u.parentId && below.has(u.parentId) && !below.has(u.id)) {
        below.add(u.id);
        grew = true;
      }
    }
  }
  const options = units.filter((u) => u.status === "active" && !below.has(u.id));
  const [parent, setParent] = useState(unit.parentId ?? "");
  return (
    <FormDialog title={t("form.move", { name: name(unit) })} busy={busy} onClose={onClose} submitLabel={t("form.moveSubmit")} onSubmit={() => onSubmit(parent || null)}>
      <p className="admin-meta">{t("form.moveHint")}</p>
      <SelectField label={t("form.newParent")} name="parent" value={parent} onChange={(e) => setParent(e.target.value)} options={[{ value: "", label: t("unit.top") }, ...options.map((u) => ({ value: u.id, label: name(u) }))]} />
    </FormDialog>
  );
}

function DeactivateForm({ unit, units, hasChildren, hasMembers, name, busy, onClose, onSubmit }: {
  unit: OrgUnitView;
  units: OrgUnitView[];
  hasChildren: boolean;
  hasMembers: boolean;
  name: (u: OrgUnitView) => string;
  busy: boolean;
  onClose: () => void;
  onSubmit: (body: { childrenAction: string | null; membershipsAction: string | null; moveMembershipsTo: string | null }) => void;
}) {
  const t = useTranslations("admin.org");
  const [children, setChildren] = useState("move_to_parent");
  const [memberships, setMemberships] = useState("end");
  const [target, setTarget] = useState("");
  const targets = units.filter((u) => u.status === "active" && u.id !== unit.id);
  return (
    <FormDialog
      title={t("form.deactivate", { name: name(unit) })}
      danger
      busy={busy}
      onClose={onClose}
      submitLabel={t("form.deactivateSubmit")}
      onSubmit={() =>
        onSubmit({
          childrenAction: hasChildren ? children : null,
          membershipsAction: hasMembers || hasChildren ? memberships : null,
          moveMembershipsTo: memberships === "move" ? target || null : null,
        })
      }
    >
      <p className="admin-meta">{t("form.deactivateHint")}</p>
      {hasChildren ? (
        <RadioGroup
          legend={t("form.childrenAction")}
          name="childrenAction"
          value={children}
          onChange={(e) => setChildren(e.target.value)}
          options={[
            { value: "move_to_parent", label: t("form.childrenMove") },
            { value: "deactivate", label: t("form.childrenDeactivate") },
          ]}
        />
      ) : null}
      {hasMembers || hasChildren ? (
        <>
          <RadioGroup
            legend={t("form.membershipsAction")}
            name="membershipsAction"
            value={memberships}
            onChange={(e) => setMemberships(e.target.value)}
            options={[
              { value: "end", label: t("form.membershipsEnd") },
              { value: "move", label: t("form.membershipsMove") },
            ]}
          />
          {memberships === "move" ? (
            <SelectField label={t("form.moveMembershipsTo")} name="target" required value={target} onChange={(e) => setTarget(e.target.value)} options={[{ value: "", label: t("form.pick") }, ...targets.map((u) => ({ value: u.id, label: name(u) }))]} />
          ) : null}
        </>
      ) : null}
    </FormDialog>
  );
}

function MemberForm({ unitName, busy, onClose, onSubmit }: { unitName: string; busy: boolean; onClose: () => void; onSubmit: (userId: string, type: string) => void }) {
  const t = useTranslations("admin.org");
  const [userId, setUserId] = useState<string | null>(null);
  const [type, setType] = useState<string>("secondary");
  return (
    <FormDialog title={t("members.addTo", { name: unitName })} busy={busy} onClose={onClose} submitLabel={t("members.addSubmit")} onSubmit={() => userId && onSubmit(userId, type)}>
      <PersonPicker label={t("members.person")} value={userId} onChange={setUserId} />
      <SelectField label={t("members.type")} name="type" value={type} onChange={(e) => setType(e.target.value)} options={MEMBERSHIP_TYPES.map((m) => ({ value: m, label: t(`membershipTypes.${m}`) }))} />
      <p className="admin-meta">{t("members.primaryHint")}</p>
    </FormDialog>
  );
}

function TypeForm({ busy, onClose, onSubmit }: { busy: boolean; onClose: () => void; onSubmit: (key: string, names: Record<string, string>) => void }) {
  const t = useTranslations("admin.org");
  const [key, setKey] = useState("");
  const [names, setNames] = useState<Record<string, string>>({ en: "", ar: "" });
  return (
    <FormDialog title={t("typeAdd")} busy={busy} onClose={onClose} submitLabel={t("form.create")} onSubmit={() => onSubmit(key.trim(), Object.fromEntries(Object.entries(names).filter(([, v]) => v.trim())))}>
      <FormField label={t("typeKey")} hint={t("typeKeyHint")} name="key" dir="ltr" required value={key} onChange={(e) => setKey(e.target.value)} />
      {NAME_LOCALES.map((l) => (
        <FormField key={l} label={t(`unit.name_${l}`)} name={`type-${l}`} dir={l === "ar" ? "rtl" : "ltr"} value={names[l] ?? ""} onChange={(e) => setNames({ ...names, [l]: e.target.value })} />
      ))}
    </FormDialog>
  );
}
