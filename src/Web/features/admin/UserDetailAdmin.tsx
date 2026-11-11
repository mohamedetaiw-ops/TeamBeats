"use client";

import Link from "next/link";
import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable } from "@/components/ui/DataTable";
import { FactList, ProfileBlock, ProfileLayout } from "@/components/ui/Patterns";
import { membershipStatusName, scopeTypeName, unitLabel, type OrgUnitView, type UserDetail } from "@/lib/admin/types";
import { ResultAlert, useAdminAction, useWhen } from "./shared";
import { userState } from "./UsersAdmin";

/**
 * One person in administration (spec 006 FR-009, spec 003 US3): who they are, whether they can sign in, their units
 * and roles, and the two actions on their account: disable or enable the membership, and sign them out everywhere.
 * The API refuses what the administrator may not do, such as disabling themselves or the last administrator.
 */
export function UserDetailAdmin({ initial, units, roleName }: { initial: UserDetail; units: OrgUnitView[]; roleName: Record<string, string> }) {
  const t = useTranslations("admin.users");
  const locale = useLocale();
  const when = useWhen();
  const returnTo = `/admin/users/${initial.user.id}`;
  const { busy, result, run } = useAdminAction(returnTo);
  const [detail, setDetail] = useState(initial);
  const unit = (id: string | null) => {
    const u = id ? units.find((x) => x.id === id) : undefined;
    return u ? unitLabel(u, locale) : t("unknownUnit");
  };
  const membership = membershipStatusName(detail.tenantMembership?.status);
  const state = userState({ status: detail.user.status, membershipStatus: membership });

  async function reload() {
    const response = await fetch(`/api/v1/admin/users/${detail.user.id}`, { cache: "no-store" });
    if (response.ok) setDetail((await response.json()) as UserDetail);
  }

  const scope = (type: string | number, id: string | null) => {
    const name = scopeTypeName(type);
    return name === "tenant" ? t("scopes.tenant") : t(`scopes.${name === "org_subtree" ? "org_subtree" : name === "org_unit" ? "org_unit" : "resource"}`, { name: unit(id) });
  };

  return (
    <div className="admin-screen">
      <ResultAlert result={result} />
      <ProfileLayout
        title={detail.user.displayName}
        description={<bdi dir="ltr">{detail.user.email}</bdi>}
        status={{ text: t(`states.${state}`), tone: state === "active" ? "success" : "danger" }}
        facts={[
          { key: "membership", label: t("membership"), value: membership ? t(`membershipStates.${membership}`) : t("noMembership") },
          { key: "type", label: t("membershipType"), value: detail.tenantMembership ? t(`membershipTypes.${detail.tenantMembership.membershipType}`) : "" },
          { key: "last", label: t("lastSignIn"), value: when(detail.user.lastSignInAt) },
          { key: "locale", label: t("language"), value: detail.user.preferredLocale ?? t("defaultLanguage") },
        ]}
        actions={
          <>
            {membership === "disabled" ? (
              <Button
                variant="primary"
                busy={busy === "enable"}
                busyLabel={t("working")}
                onClick={async () => {
                  const outcome = await run("enable", "POST", `/api/v1/admin/users/${detail.user.id}:enable`, undefined, t("enabled"));
                  if (outcome.ok) await reload();
                }}
              >
                {t("enable")}
              </Button>
            ) : membership === "active" ? (
              <ConfirmAction
                label={t("disable")}
                question={t("disableQuestion", { name: detail.user.displayName })}
                confirmLabel={t("disableYes")}
                cancelLabel={t("cancel")}
                onConfirm={async () => {
                  const outcome = await run("disable", "POST", `/api/v1/admin/users/${detail.user.id}:disable`, undefined, t("disabled"));
                  if (outcome.ok) await reload();
                }}
              />
            ) : null}
            <ConfirmAction
              label={t("signOut")}
              question={t("signOutQuestion", { name: detail.user.displayName })}
              confirmLabel={t("signOutYes")}
              cancelLabel={t("cancel")}
              onConfirm={async () => {
                await run<{ revoked: number }>("signout", "POST", `/api/v1/identity/users/${detail.user.id}/sessions/revoke-all`, undefined, (d) => t("signedOut", { count: d.revoked }));
              }}
            />
          </>
        }
        aside={
          <ProfileBlock title={t("access")}>
            <p className="admin-meta">{t("accessHint")}</p>
            <Link href={`/admin/roles?user=${detail.user.id}`}>{t("manageRoles")}</Link>
          </ProfileBlock>
        }
      >
        <section aria-labelledby="user-units-title" className="admin-block">
          <h2 id="user-units-title">{t("units")}</h2>
          <DataTable
            caption={t("units")}
            rows={detail.organizationMemberships}
            rowKey={(m) => m.membershipId}
            emptyTitle={t("noUnits")}
            density="compact"
            columns={[
              { key: "unit", header: t("unit"), rowHeader: true, cell: (m) => <bdi>{unit(m.orgUnitId)}</bdi> },
              { key: "type", header: t("membershipKind"), cell: (m) => t(`orgMembershipTypes.${m.type}`) },
              { key: "primary", header: t("primary"), cell: (m) => (m.isPrimary ? <Badge tone="accent">{t("primary")}</Badge> : null) },
            ]}
          />
        </section>
        <section aria-labelledby="user-roles-title" className="admin-block">
          <h2 id="user-roles-title">{t("roles")}</h2>
          <DataTable
            caption={t("roles")}
            rows={detail.roleAssignments}
            rowKey={(r) => r.assignmentId}
            emptyTitle={t("noRoles")}
            density="compact"
            columns={[
              { key: "role", header: t("role"), rowHeader: true, cell: (r) => roleName[r.roleKey] ?? r.roleKey },
              { key: "scope", header: t("scope"), cell: (r) => scope(r.scopeType, r.scopeId) },
            ]}
          />
        </section>
        <FactList facts={[{ key: "id", label: t("userId"), value: <code dir="ltr">{detail.user.id}</code> }]} />
      </ProfileLayout>
    </div>
  );
}
