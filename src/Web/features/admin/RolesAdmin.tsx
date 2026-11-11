"use client";

import { useId, useMemo, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Accordion } from "@/components/ui/Accordion";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable } from "@/components/ui/DataTable";
import { Dialog } from "@/components/ui/Dialog";
import { FormField, SelectField } from "@/components/ui/FormField";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { Tabs } from "@/components/ui/Tabs";
import { PersonPicker } from "@/features/services/PersonPicker";
import { byArea, customRoleName, scopeTypeName, unitLabel, type AssignmentView, type DecisionReason, type ExplainResult, type OrgUnitView, type RoleView } from "@/lib/admin/types";
import { ResultAlert, useAdminAction, useKeyLabel, usePeopleNames, useWhen } from "./shared";

const RETURN = "/admin/roles";

/**
 * Roles and access (spec 004): the roles of the workspace with their permissions, who holds which role where, and a
 * check that explains why someone can or cannot do something (spec 004 US6). Assigning and removing a role ask for a
 * fresh sign-in; the API also refuses to grant more than the administrator holds (spec 004 FR-007).
 */
export function RolesAdmin({
  initialRoles,
  initialAssignments,
  units,
  roleName,
  focusUser,
}: {
  initialRoles: RoleView[];
  initialAssignments: AssignmentView[] | null;
  units: OrgUnitView[];
  roleName: Record<string, string>;
  focusUser: string | null;
}) {
  const t = useTranslations("admin.roles");
  const locale = useLocale();
  const when = useWhen();
  const area = useKeyLabel("admin.areas");
  const { busy, result, run } = useAdminAction(focusUser ? `${RETURN}?user=${focusUser}` : RETURN);
  const [roles, setRoles] = useState(initialRoles);
  const [assignments, setAssignments] = useState(initialAssignments);
  const [onlyUser, setOnlyUser] = useState<string | null>(focusUser);
  const [defining, setDefining] = useState(false);
  const [assigning, setAssigning] = useState(false);
  const person = usePeopleNames([...(assignments ?? []).filter((a) => a.subjectType === "user").map((a) => a.subjectId), onlyUser]);
  const permissions = useMemo(() => [...new Set(roles.flatMap((r) => r.permissions))].sort(), [roles]);
  const nameOf = (key: string) => {
    const role = roles.find((r) => r.key === key);
    return roleName[key] ?? (role ? customRoleName(role, locale) : null) ?? key;
  };
  const unitName = (id: string | null) => {
    const u = id ? units.find((x) => x.id === id) : undefined;
    return u ? unitLabel(u, locale) : t("unknownUnit");
  };
  const where = (type: string | number | null | undefined, id: string | null | undefined) => {
    const s = scopeTypeName(type);
    if (s === "tenant") return t("scopes.tenant");
    if (s === "resource") return t("scopes.resource");
    return t(s === "org_subtree" ? "scopes.org_subtree" : "scopes.org_unit", { name: unitName(id ?? null) });
  };

  async function reloadAssignments() {
    const response = await fetch("/api/v1/authz/assignments", { cache: "no-store" });
    if (response.ok) setAssignments((await response.json()) as AssignmentView[]);
  }

  const shown = (assignments ?? []).filter((a) => !onlyUser || (a.subjectType === "user" && a.subjectId === onlyUser));

  const rolesTab = (
    <section className="admin-panel" aria-labelledby="roles-list-title">
      <div className="admin-panel-head">
        <h2 id="roles-list-title">{t("rolesTitle")}</h2>
        <Button size="sm" icon="plus" onClick={() => setDefining(true)}>
          {t("define")}
        </Button>
      </div>
      <p className="admin-meta">{t("rolesIntro")}</p>
      <Accordion
        headingLevel={3}
        items={roles.map((r) => ({
          key: r.id,
          title: t("roleTitle", { name: nameOf(r.key), count: r.permissions.length }),
          content: (
            <div className="admin-role">
              <div className="admin-badges">
                <Badge tone={r.isBuiltIn ? "neutral" : "accent"}>{t(r.isBuiltIn ? "builtIn" : "custom")}</Badge>
                {r.isDelegable ? <Badge tone="info">{t("delegable")}</Badge> : null}
                <code dir="ltr">{r.key}</code>
              </div>
              {byArea(r.permissions, (p) => p).map(([a, list]) => (
                <div key={a} className="admin-permission-group">
                  <h4>{area(a)}</h4>
                  <ul className="admin-chips">
                    {list.map((p) => (
                      <li key={p}>
                        <code dir="ltr">{p}</code>
                      </li>
                    ))}
                  </ul>
                </div>
              ))}
            </div>
          ),
        }))}
      />
    </section>
  );

  const assignmentsTab =
    assignments === null ? (
      <Alert tone="info" title={t("assignmentsDenied")} />
    ) : (
      <section className="admin-panel" aria-labelledby="roles-assign-title">
        <div className="admin-panel-head">
          <h2 id="roles-assign-title">{t("assignmentsTitle")}</h2>
          <Button size="sm" icon="plus" variant="primary" onClick={() => setAssigning(true)}>
            {t("assign")}
          </Button>
        </div>
        <p className="admin-meta">{t("assignmentsIntro")}</p>
        {onlyUser ? (
          <div className="admin-row">
            <Badge tone="accent">{t("onlyFor", { name: person(onlyUser) })}</Badge>
            <Button size="xs" variant="quiet" onClick={() => setOnlyUser(null)}>
              {t("showEveryone")}
            </Button>
          </div>
        ) : null}
        <DataTable
          caption={t("assignmentsTitle")}
          rows={shown}
          rowKey={(a) => a.id}
          emptyTitle={t("noAssignments")}
          defaultSort={{ key: "role", direction: "ascending" }}
          columns={[
            {
              key: "who",
              header: t("who"),
              rowHeader: true,
              sortValue: (a) => (a.subjectType === "user" ? person(a.subjectId) : a.subjectId),
              cell: (a) => (a.subjectType === "user" ? <bdi>{person(a.subjectId)}</bdi> : t("group", { id: a.subjectId.slice(0, 8) })),
            },
            { key: "role", header: t("role"), sortValue: (a) => nameOf(a.roleKey), cell: (a) => nameOf(a.roleKey) },
            { key: "where", header: t("where"), cell: (a) => <bdi>{where(a.scopeType, a.scopeId)}</bdi> },
            { key: "since", header: t("since"), sortValue: (a) => a.createdAt, cell: (a) => when(a.createdAt, "short"), hideable: true },
            {
              key: "actions",
              header: t("actions"),
              cell: (a) => (
                <ConfirmAction
                  label={t("revoke")}
                  question={t("revokeQuestion", { role: nameOf(a.roleKey), name: a.subjectType === "user" ? person(a.subjectId) : a.subjectId.slice(0, 8) })}
                  confirmLabel={t("revokeYes")}
                  cancelLabel={t("cancel")}
                  onConfirm={async () => {
                    const outcome = await run("revoke", "DELETE", `/api/v1/authz/assignments/${a.id}`, undefined, t("revoked"));
                    if (outcome.ok) await reloadAssignments();
                  }}
                />
              ),
            },
          ]}
        />
      </section>
    );

  return (
    <div className="admin-screen">
      <ResultAlert result={result} />
      <Tabs
        label={t("tabs")}
        tabs={[
          { key: "assignments", label: t("assignmentsTitle"), icon: "people", content: assignmentsTab },
          { key: "roles", label: t("rolesTitle"), icon: "lock", content: rolesTab },
          { key: "explain", label: t("explainTitle"), icon: "search", content: <Explain permissions={permissions} units={units} where={where} nameOf={nameOf} /> },
        ]}
      />

      {defining ? (
        <DefineRole
          permissions={permissions}
          busy={busy === "define"}
          onClose={() => setDefining(false)}
          onSubmit={async (body) => {
            const outcome = await run<RoleView>("define", "POST", "/api/v1/authz/roles", body, t("defined"));
            if (outcome.ok) {
              setDefining(false);
              setRoles((before) => [...before, outcome.data]);
            }
          }}
        />
      ) : null}

      {assigning ? (
        <AssignRole
          roles={roles}
          nameOf={nameOf}
          units={units}
          initialUser={onlyUser}
          busy={busy === "assign"}
          onClose={() => setAssigning(false)}
          onSubmit={async (body) => {
            const outcome = await run("assign", "POST", "/api/v1/authz/assignments", body, t("assigned"));
            if (outcome.ok) {
              setAssigning(false);
              await reloadAssignments();
            }
          }}
        />
      ) : null}
    </div>
  );
}

function DefineRole({
  permissions,
  busy,
  onClose,
  onSubmit,
}: {
  permissions: string[];
  busy: boolean;
  onClose: () => void;
  onSubmit: (body: { key: string; names: Record<string, string>; permissions: string[]; delegable: boolean }) => void;
}) {
  const t = useTranslations("admin.roles");
  const area = useKeyLabel("admin.areas");
  const formId = useId();
  const [key, setKey] = useState("");
  const [names, setNames] = useState({ en: "", ar: "" });
  const [chosen, setChosen] = useState<string[]>([]);
  const [delegable, setDelegable] = useState(false);
  return (
    <Dialog
      open
      size="lg"
      onClose={onClose}
      title={t("defineTitle")}
      description={t("defineIntro")}
      footer={
        <>
          <Button onClick={onClose}>{t("cancel")}</Button>
          <Button type="submit" form={formId} variant="primary" busy={busy} busyLabel={t("working")} disabled={chosen.length === 0}>
            {t("defineSubmit")}
          </Button>
        </>
      }
    >
      <form
        id={formId}
        className="admin-form"
        onSubmit={(e) => {
          e.preventDefault();
          onSubmit({ key: key.trim(), names: Object.fromEntries(Object.entries(names).filter(([, v]) => v.trim())), permissions: chosen, delegable });
        }}
      >
        <FormField label={t("key")} hint={t("keyHint")} name="key" dir="ltr" required value={key} onChange={(e) => setKey(e.target.value)} />
        <div className="admin-fields">
          <FormField label={t("nameEn")} name="nameEn" dir="ltr" value={names.en} onChange={(e) => setNames({ ...names, en: e.target.value })} />
          <FormField label={t("nameAr")} name="nameAr" dir="rtl" value={names.ar} onChange={(e) => setNames({ ...names, ar: e.target.value })} />
        </div>
        <Checkbox name="delegable" label={t("delegableLabel")} hint={t("delegableHint")} checked={delegable} onChange={(e) => setDelegable(e.target.checked)} />
        <p className="admin-meta">{t("permissionsHint", { count: chosen.length })}</p>
        {byArea(permissions, (p) => p).map(([a, list]) => (
          <fieldset key={a} className="admin-fieldset">
            <legend>{area(a)}</legend>
            <div className="admin-choices">
              {list.map((p) => (
                <Checkbox
                  key={p}
                  name="permissions"
                  value={p}
                  label={<code dir="ltr">{p}</code>}
                  checked={chosen.includes(p)}
                  onChange={(e) => setChosen(e.target.checked ? [...chosen, p] : chosen.filter((x) => x !== p))}
                />
              ))}
            </div>
          </fieldset>
        ))}
      </form>
    </Dialog>
  );
}

function AssignRole({
  roles,
  nameOf,
  units,
  initialUser,
  busy,
  onClose,
  onSubmit,
}: {
  roles: RoleView[];
  nameOf: (key: string) => string;
  units: OrgUnitView[];
  initialUser: string | null;
  busy: boolean;
  onClose: () => void;
  onSubmit: (body: { subjectType: "user"; subjectId: string; roleKey: string; scopeType: string; scopeId: string | null; scopeResourceType: null }) => void;
}) {
  const t = useTranslations("admin.roles");
  const locale = useLocale();
  const formId = useId();
  const [user, setUser] = useState<string | null>(initialUser);
  const [role, setRole] = useState(roles.find((r) => r.key !== "employee")?.key ?? roles[0]?.key ?? "");
  const [scope, setScope] = useState("tenant");
  const [unit, setUnit] = useState("");
  const activeUnits = units.filter((u) => u.status === "active");
  return (
    <Dialog
      open
      onClose={onClose}
      title={t("assignTitle")}
      description={t("assignIntro")}
      footer={
        <>
          <Button onClick={onClose}>{t("cancel")}</Button>
          <Button type="submit" form={formId} variant="primary" busy={busy} busyLabel={t("working")} disabled={!user || (scope !== "tenant" && !unit)}>
            {t("assignSubmit")}
          </Button>
        </>
      }
    >
      <form
        id={formId}
        className="admin-form"
        onSubmit={(e) => {
          e.preventDefault();
          if (!user) return;
          onSubmit({ subjectType: "user", subjectId: user, roleKey: role, scopeType: scope, scopeId: scope === "tenant" ? null : unit, scopeResourceType: null });
        }}
      >
        <PersonPicker label={t("who")} value={user} onChange={setUser} />
        <SelectField label={t("role")} name="role" value={role} onChange={(e) => setRole(e.target.value)} options={roles.map((r) => ({ value: r.key, label: nameOf(r.key) }))} />
        <RadioGroup
          legend={t("where")}
          name="scope"
          value={scope}
          onChange={(e) => setScope(e.target.value)}
          options={[
            { value: "tenant", label: t("scopes.tenant") },
            { value: "org_subtree", label: t("scopeSubtree") },
            { value: "org_unit", label: t("scopeUnit") },
          ]}
        />
        {scope !== "tenant" ? (
          <SelectField label={t("unit")} name="unit" required value={unit} onChange={(e) => setUnit(e.target.value)} options={[{ value: "", label: t("pickUnit") }, ...activeUnits.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]} />
        ) : null}
      </form>
    </Dialog>
  );
}

function Explain({
  permissions,
  units,
  where,
  nameOf,
}: {
  permissions: string[];
  units: OrgUnitView[];
  where: (type: string | number | null | undefined, id: string | null | undefined) => string;
  nameOf: (key: string) => string;
}) {
  const t = useTranslations("admin.roles");
  const locale = useLocale();
  const [user, setUser] = useState<string | null>(null);
  const [permission, setPermission] = useState(permissions[0] ?? "");
  const [unit, setUnit] = useState("");
  const [answer, setAnswer] = useState<ExplainResult | null>(null);
  const [failed, setFailed] = useState(false);
  const [busy, setBusy] = useState(false);

  async function check() {
    if (!user) return;
    setBusy(true);
    setFailed(false);
    setAnswer(null);
    try {
      const q = new URLSearchParams({ userId: user, permission });
      if (unit) q.set("orgUnitId", unit);
      const response = await fetch(`/api/v1/authz/explain?${q}`, { cache: "no-store" });
      if (response.ok) setAnswer((await response.json()) as ExplainResult);
      else setFailed(true);
    } catch {
      setFailed(true);
    } finally {
      setBusy(false);
    }
  }

  const reason = (r: DecisionReason) => {
    switch (r.kind) {
      case "role_assignment":
        return t("reasons.role_assignment", { role: nameOf(r.roleKey ?? ""), where: where(r.scopeType, r.scopeId) });
      case "inheritance_rule":
        return t("reasons.inheritance_rule", { where: where(r.scopeType, r.scopeId) });
      case "resource_acl":
        return t("reasons.resource_acl");
      case "tenant_visibility":
        return t("reasons.tenant_visibility");
      default:
        return r.kind;
    }
  };

  return (
    <section className="admin-panel" aria-labelledby="roles-explain-title">
      <h2 id="roles-explain-title">{t("explainTitle")}</h2>
      <p className="admin-meta">{t("explainIntro")}</p>
      <form
        className="admin-form"
        onSubmit={(e) => {
          e.preventDefault();
          void check();
        }}
      >
        <PersonPicker label={t("who")} value={user} onChange={setUser} />
        <div className="admin-fields">
          <SelectField label={t("permission")} name="permission" value={permission} onChange={(e) => setPermission(e.target.value)} options={permissions.map((p) => ({ value: p, label: p }))} />
          <SelectField label={t("inUnit")} name="explainUnit" value={unit} onChange={(e) => setUnit(e.target.value)} options={[{ value: "", label: t("scopes.tenant") }, ...units.filter((u) => u.status === "active").map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]} />
        </div>
        <div className="admin-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")} disabled={!user}>
            {t("check")}
          </Button>
        </div>
      </form>
      {failed ? <Alert tone="danger" title={t("checkFailed")} live /> : null}
      {answer ? (
        <Alert tone={answer.allowed ? "success" : "warning"} title={t(answer.allowed ? "allowed" : "notAllowed")} live>
          {answer.reasons.length > 0 ? (
            <ul>
              {answer.reasons.map((r, i) => (
                <li key={`r-${i}`}>{reason(r)}</li>
              ))}
            </ul>
          ) : (
            <p>{t("noGrant")}</p>
          )}
          {answer.notApplied.length > 0 ? (
            <>
              <p>{t("notApplied")}</p>
              <ul>
                {answer.notApplied.map((r, i) => (
                  <li key={`n-${i}`}>{reason(r)}</li>
                ))}
              </ul>
            </>
          ) : null}
        </Alert>
      ) : null}
    </section>
  );
}
