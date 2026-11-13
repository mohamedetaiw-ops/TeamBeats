"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { NumberField } from "@/components/ui/Inputs";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { ResultAlert, useAdminAction, useWhen } from "@/features/admin/shared";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import {
  isConfidential,
  isDelegated,
  scopeLabelKey,
  scopesFor,
  type ApiAppDetail,
  type ApiScope,
  type AppRoleAssignment,
  type IssuedCredential,
  type RoleSummary,
} from "@/lib/api-access";
import "./api-access.css";

/**
 * One app for API administrators (spec 035 US1, US6, FR-004, FR-005, FR-011, FR-013): its registration, credentials
 * (a secret shown once, or a public key for private key JWT), the scopes the administrator consented to for everyone,
 * and, for an app that acts as itself, the roles it holds and where. Every change is checked again by the API.
 */
export function AppDetailAdmin({
  initial,
  scopes,
  assignments: initialAssignments,
  roles,
  roleNames,
  units,
}: {
  initial: ApiAppDetail;
  scopes: ApiScope[];
  assignments: AppRoleAssignment[];
  roles: RoleSummary[];
  roleNames: Record<string, string>;
  units: OrgUnit[];
}) {
  const t = useTranslations("apiAccess.admin");
  const [detail, setDetail] = useState(initial);
  const [issued, setIssued] = useState<IssuedCredential | null>(null);
  const [assignments, setAssignments] = useState(initialAssignments);
  const action = useAdminAction(`/admin/api/apps/${initial.app.id}`);
  const app = detail.app;

  async function reload() {
    const response = await fetch(`/api/v1/api-access/apps/${app.id}`, { cache: "no-store" }).catch(() => null);
    if (response?.ok) setDetail((await response.json()) as ApiAppDetail);
  }

  return (
    <div className="admin-screen">
      <ResultAlert result={action.result} />
      {issued?.secret ? (
        <Alert tone="success" title={t("secretTitle")} live action={<Button size="sm" variant="secondary" onClick={() => setIssued(null)}>{t("secretDone")}</Button>}>
          <p>{t("secretOnce")}</p>
          <p className="api-access-secret" dir="ltr">
            <code>{issued.secret}</code>
          </p>
        </Alert>
      ) : null}
      <section className="admin-panel" aria-labelledby="api-app-summary">
        <div className="admin-panel-head">
          <h2 id="api-app-summary">{t("summaryTitle")}</h2>
          <Badge tone={app.status === "active" ? "success" : "neutral"}>{t(`statuses.${app.status}`)}</Badge>
        </div>
        <dl className="api-access-facts">
          <dt>{t("kind")}</dt>
          <dd>{t(`kinds.${app.kind}`)}</dd>
          <dt>{t("clientId")}</dt>
          <dd>
            <code dir="ltr">{app.clientId}</code>
          </dd>
        </dl>
        <div className="admin-actions">
          {app.status === "active" ? (
            <ConfirmAction
              label={t("disable")}
              question={t("disableQuestion", { name: app.name })}
              confirmLabel={t("disable")}
              cancelLabel={t("cancel")}
              onConfirm={async () => {
                const outcome = await action.run("disable", "POST", `/api/v1/api-access/apps/${app.id}:disable`, undefined, t("disabled"));
                if (outcome.ok) await reload();
              }}
            />
          ) : (
            <Button
              size="sm"
              variant="secondary"
              busy={action.busy === "enable"}
              busyLabel={t("saving")}
              onClick={async () => {
                const outcome = await action.run("enable", "POST", `/api/v1/api-access/apps/${app.id}:enable`, undefined, t("enabled"));
                if (outcome.ok) await reload();
              }}
            >
              {t("enable")}
            </Button>
          )}
        </div>
      </section>
      <EditApp detail={detail} scopes={scopes} action={action} onSaved={reload} />
      {isConfidential(app.kind) ? (
        <Credentials
          detail={detail}
          action={action}
          onIssued={async (c) => {
            setIssued(c);
            await reload();
          }}
          onChanged={reload}
        />
      ) : null}
      {isDelegated(app.kind) ? <AdminConsent detail={detail} action={action} onSaved={reload} /> : null}
      {app.kind === "application" ? (
        <AppRoles
          appId={app.id}
          assignments={assignments}
          roles={roles.filter((r) => r.assignableToApps)}
          roleNames={roleNames}
          units={units}
          action={action}
          onChanged={setAssignments}
        />
      ) : null}
    </div>
  );
}

type Action = ReturnType<typeof useAdminAction>;

function ScopeChoices({ offered, chosen, onChange, prefix }: { offered: ApiScope[]; chosen: string[]; onChange: (next: string[]) => void; prefix: string }) {
  const describe = useTranslations("apiAccess.scope");
  return (
    <div className="api-access-scopes">
      {offered.map((s) => (
        <Checkbox
          key={s.key}
          name={`${prefix}-${s.key}`}
          label={<code dir="ltr">{s.key}</code>}
          hint={describe(scopeLabelKey(s.key))}
          checked={chosen.includes(s.key)}
          onChange={(e) => onChange(e.target.checked ? [...chosen, s.key] : chosen.filter((k) => k !== s.key))}
        />
      ))}
    </div>
  );
}

function EditApp({ detail, scopes, action, onSaved }: { detail: ApiAppDetail; scopes: ApiScope[]; action: Action; onSaved: () => Promise<void> }) {
  const t = useTranslations("apiAccess.admin");
  const app = detail.app;
  const [name, setName] = useState(app.name);
  const [publisher, setPublisher] = useState(app.publisher ?? "");
  const [description, setDescription] = useState(app.description ?? "");
  const [chosen, setChosen] = useState(app.scopes);
  const [redirects, setRedirects] = useState(app.redirectUris.join("\n"));
  const [guests, setGuests] = useState(app.allowGuests);

  async function save() {
    const outcome = await action.run(
      "edit",
      "PUT",
      `/api/v1/api-access/apps/${app.id}`,
      {
        name,
        publisher,
        description,
        scopes: chosen,
        redirectUris: isDelegated(app.kind) ? redirects.split(/\s+/).filter(Boolean) : [],
        allowGuests: guests,
        version: app.version,
      },
      t("saved"),
    );
    if (outcome.ok) await onSaved();
  }

  return (
    <section className="admin-panel" aria-labelledby="api-app-edit">
      <h2 id="api-app-edit">{t("registrationTitle")}</h2>
      <form
        className="admin-form"
        onSubmit={(e) => {
          e.preventDefault();
          void save();
        }}
      >
        <div className="admin-fields">
          <FormField label={t("name")} name="name" required maxLength={80} value={name} onChange={(e) => setName(e.target.value)} />
          <FormField label={t("publisher")} hint={t("publisherHint")} name="publisher" maxLength={80} value={publisher} onChange={(e) => setPublisher(e.target.value)} />
        </div>
        <TextAreaField label={t("description")} name="description" rows={2} value={description} onChange={(e) => setDescription(e.target.value)} />
        <fieldset className="admin-fieldset">
          <legend>{t("scopes")}</legend>
          <p className="admin-meta">{t("scopesHint")}</p>
          <ScopeChoices offered={scopesFor(app.kind, scopes)} chosen={chosen} onChange={setChosen} prefix="scope" />
        </fieldset>
        {isDelegated(app.kind) ? (
          <>
            <TextAreaField label={t("redirects")} hint={t("redirectsHint")} name="redirects" dir="ltr" rows={3} value={redirects} onChange={(e) => setRedirects(e.target.value)} />
            <Checkbox name="allowGuests" label={t("allowGuests")} hint={t("allowGuestsHint")} checked={guests} onChange={(e) => setGuests(e.target.checked)} />
          </>
        ) : null}
        <div className="admin-row">
          <Button type="submit" variant="primary" busy={action.busy === "edit"} busyLabel={t("saving")}>
            {t("save")}
          </Button>
        </div>
      </form>
    </section>
  );
}

function Credentials({
  detail,
  action,
  onIssued,
  onChanged,
}: {
  detail: ApiAppDetail;
  action: Action;
  onIssued: (c: IssuedCredential) => Promise<void>;
  onChanged: () => Promise<void>;
}) {
  const t = useTranslations("apiAccess.admin");
  const when = useWhen();
  const app = detail.app;
  const [kind, setKind] = useState<"public_key" | "secret">("public_key");
  const [months, setMonths] = useState<number | null>(6);
  const [publicKey, setPublicKey] = useState("");
  const [keyError, setKeyError] = useState<string | null>(null);

  async function add() {
    let parsed: unknown = undefined;
    if (kind === "public_key") {
      try {
        parsed = JSON.parse(publicKey);
        setKeyError(null);
      } catch {
        setKeyError(t("publicKeyNotJson"));
        return;
      }
    }
    const outcome = await action.run<IssuedCredential>(
      "credential",
      "POST",
      `/api/v1/api-access/apps/${app.id}/credentials`,
      { kind, expiresInMonths: months, publicKey: parsed },
      kind === "secret" ? t("secretAdded") : t("keyAdded"),
    );
    if (outcome.ok) {
      setPublicKey("");
      await onIssued(outcome.data);
    }
  }

  return (
    <section className="admin-panel" aria-labelledby="api-app-credentials">
      <h2 id="api-app-credentials">{t("credentialsTitle")}</h2>
      <p className="admin-meta">{t("credentialsIntro")}</p>
      <DataTable
        caption={t("credentialsTitle")}
        rows={detail.credentials}
        rowKey={(c) => c.id}
        emptyTitle={t("noCredentials")}
        columns={[
          { key: "kind", header: t("credentialKind"), rowHeader: true, cell: (c) => t(`credentialKinds.${c.kind}`) },
          {
            key: "hint",
            header: t("hint"),
            cell: (c) => (
              <code dir="ltr">{c.kind === "secret" ? `…${c.hint ?? ""}` : (c.hint ?? "").slice(0, 12)}</code>
            ),
          },
          { key: "expires", header: t("expires"), cell: (c) => when(c.expiresAt, "short"), sortValue: (c) => c.expiresAt },
          { key: "lastUsed", header: t("lastUsed"), cell: (c) => when(c.lastUsedAt, "short") },
          {
            key: "state",
            header: t("status"),
            cell: (c) =>
              c.revokedAt ? (
                <Badge>{t("revoked")}</Badge>
              ) : new Date(c.expiresAt) <= new Date() ? (
                <Badge tone="warning">{t("expired")}</Badge>
              ) : (
                <Badge tone="success">{t("live")}</Badge>
              ),
          },
          {
            key: "actions",
            header: t("actions"),
            align: "end",
            cell: (c) =>
              c.revokedAt ? null : (
                <ConfirmAction
                  label={t("revoke")}
                  question={t("revokeCredentialQuestion")}
                  confirmLabel={t("revoke")}
                  cancelLabel={t("cancel")}
                  onConfirm={async () => {
                    const outcome = await action.run("revoke-" + c.id, "POST", `/api/v1/api-access/apps/${app.id}/credentials/${c.id}:revoke`, undefined, t("credentialRevoked"));
                    if (outcome.ok) await onChanged();
                  }}
                />
              ),
          },
        ]}
      />
      <form
        className="admin-form"
        onSubmit={(e) => {
          e.preventDefault();
          void add();
        }}
      >
        <h3>{t("addCredential")}</h3>
        <RadioGroup
          legend={t("credentialKind")}
          name="credentialKind"
          value={kind}
          onChange={(e) => setKind(e.target.value as "public_key" | "secret")}
          options={[
            { value: "public_key", label: t("credentialKinds.public_key"), hint: t("publicKeyPreferred") },
            { value: "secret", label: t("credentialKinds.secret"), hint: t("secretHint") },
          ]}
        />
        {kind === "public_key" ? (
          <TextAreaField
            label={t("publicKey")}
            hint={t("publicKeyHint")}
            name="publicKey"
            dir="ltr"
            rows={5}
            error={keyError}
            value={publicKey}
            onChange={(e) => setPublicKey(e.target.value)}
          />
        ) : null}
        <NumberField label={t("months")} hint={t("monthsHint")} name="months" min={1} max={12} value={months} onValueChange={setMonths} />
        <div className="admin-row">
          <Button type="submit" variant="primary" busy={action.busy === "credential"} busyLabel={t("saving")}>
            {t("addCredential")}
          </Button>
        </div>
        <p className="admin-meta">{t("stepUpHint")}</p>
      </form>
    </section>
  );
}

function AdminConsent({ detail, action, onSaved }: { detail: ApiAppDetail; action: Action; onSaved: () => Promise<void> }) {
  const t = useTranslations("apiAccess.admin");
  const [chosen, setChosen] = useState(detail.adminConsentScopes);
  const app = detail.app;
  const offered = app.scopes.map((key) => ({ key, descriptionKey: "", userConsent: false, holder: "delegated" as const, capabilities: [] }));
  return (
    <section className="admin-panel" aria-labelledby="api-app-consent">
      <h2 id="api-app-consent">{t("adminConsentTitle")}</h2>
      <p className="admin-meta">{t("adminConsentIntro")}</p>
      <form
        className="admin-form"
        onSubmit={async (e) => {
          e.preventDefault();
          const outcome = await action.run("consent", "PUT", `/api/v1/api-access/apps/${app.id}/admin-consent`, { scopes: chosen }, t("adminConsentSaved"));
          if (outcome.ok) await onSaved();
        }}
      >
        <ScopeChoices offered={offered} chosen={chosen} onChange={setChosen} prefix="consent" />
        <div className="admin-row">
          <Button type="submit" variant="primary" busy={action.busy === "consent"} busyLabel={t("saving")}>
            {t("saveAdminConsent")}
          </Button>
        </div>
      </form>
    </section>
  );
}

function AppRoles({
  appId,
  assignments,
  roles,
  roleNames,
  units,
  action,
  onChanged,
}: {
  appId: string;
  assignments: AppRoleAssignment[];
  roles: RoleSummary[];
  roleNames: Record<string, string>;
  units: OrgUnit[];
  action: Action;
  onChanged: (list: AppRoleAssignment[]) => void;
}) {
  const t = useTranslations("apiAccess.admin");
  const locale = useLocale();
  const active = units.filter((u) => u.status === "active");
  const [role, setRole] = useState(roles[0]?.key ?? "");
  const [scopeType, setScopeType] = useState("org_unit");
  const [unit, setUnit] = useState(active[0]?.id ?? "");
  const roleLabel = (key: string) => roleNames[key] ?? key;
  const where = (a: AppRoleAssignment) => {
    if (a.scopeType === "tenant") return t("wholeOrganization");
    const u = units.find((x) => x.id === a.scopeId);
    const name = u ? unitName(u, locale) : t("unknownUnit");
    return a.scopeType === "org_subtree" ? t("unitAndBelow", { unit: name }) : name;
  };

  async function reload() {
    const response = await fetch(`/api/v1/authz/assignments?appId=${appId}`, { cache: "no-store" }).catch(() => null);
    if (response?.ok) onChanged((await response.json()) as AppRoleAssignment[]);
  }

  return (
    <section className="admin-panel" aria-labelledby="api-app-roles">
      <h2 id="api-app-roles">{t("rolesTitle")}</h2>
      <p className="admin-meta">{t("rolesIntro")}</p>
      <DataTable
        caption={t("rolesTitle")}
        rows={assignments}
        rowKey={(a) => a.id}
        emptyTitle={t("noRoles")}
        emptyMessage={t("noRolesHint")}
        columns={[
          { key: "role", header: t("role"), rowHeader: true, cell: (a) => roleLabel(a.roleKey) },
          { key: "where", header: t("where"), cell: where },
          {
            key: "actions",
            header: t("actions"),
            align: "end",
            cell: (a) => (
              <ConfirmAction
                label={t("removeRole")}
                question={t("removeRoleQuestion", { role: roleLabel(a.roleKey) })}
                confirmLabel={t("removeRole")}
                cancelLabel={t("cancel")}
                onConfirm={async () => {
                  const outcome = await action.run("role-" + a.id, "DELETE", `/api/v1/authz/assignments/${a.id}`, undefined, t("roleRemoved"));
                  if (outcome.ok) await reload();
                }}
              />
            ),
          },
        ]}
      />
      {roles.length > 0 ? (
        <form
          className="admin-form"
          onSubmit={async (e) => {
            e.preventDefault();
            const outcome = await action.run(
              "assign",
              "POST",
              "/api/v1/authz/assignments",
              { subjectType: "app", subjectId: appId, roleKey: role, scopeType, scopeId: scopeType === "tenant" ? null : unit },
              t("roleGiven"),
            );
            if (outcome.ok) await reload();
          }}
        >
          <h3>{t("giveRole")}</h3>
          <div className="admin-fields">
            <SelectField label={t("role")} name="role" value={role} onChange={(e) => setRole(e.target.value)} options={roles.map((r) => ({ value: r.key, label: roleLabel(r.key) }))} />
            <SelectField
              label={t("where")}
              name="scopeType"
              value={scopeType}
              onChange={(e) => setScopeType(e.target.value)}
              options={["org_unit", "org_subtree", "tenant"].map((s) => ({ value: s, label: t(`scopeTypes.${s}`) }))}
            />
            {scopeType !== "tenant" ? (
              <SelectField label={t("unit")} name="unit" value={unit} onChange={(e) => setUnit(e.target.value)} options={active.map((u) => ({ value: u.id, label: unitName(u, locale) }))} />
            ) : null}
          </div>
          <div className="admin-row">
            <Button type="submit" variant="primary" busy={action.busy === "assign"} busyLabel={t("saving")}>
              {t("giveRole")}
            </Button>
          </div>
          <p className="admin-meta">{t("stepUpHint")}</p>
        </form>
      ) : null}
    </section>
  );
}
