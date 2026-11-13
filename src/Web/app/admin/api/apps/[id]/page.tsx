import Link from "next/link";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { ApiAppDetail, ApiScope, AppRoleAssignment, EventCatalogueEntry, EventSubscription, RoleSummary } from "@/lib/api-access";
import { listUnits } from "@/lib/experience/client";
import { roleNames } from "@/features/admin/roleNames";
import { AppDetailAdmin } from "@/features/api-access/AppDetailAdmin";

/** One app's registration, credentials, admin consent, roles and event subscriptions (spec 035 US1, US4, US6, FR-005, FR-011, FR-018). */
export default async function ApiAppPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!/^[0-9a-f-]{36}$/i.test(id)) {
    notFound();
  }
  const [detail, scopes] = await Promise.all([apiGet<ApiAppDetail>(`/api/v1/api-access/apps/${id}`), apiGet<ApiScope[]>("/api/v1/api-access/scopes")]);
  if (!detail.ok || !scopes.ok) {
    notFound();
  }
  // Roles are for apps that act as themselves; people who may not read roles still see the rest of the page.
  const application = detail.data.app.kind === "application";
  const [assignments, roles, units, subscriptions, catalogue] = application
    ? await Promise.all([
        apiGet<AppRoleAssignment[]>(`/api/v1/authz/assignments?appId=${id}`),
        apiGet<RoleSummary[]>("/api/v1/authz/roles"),
        listUnits(),
        apiGet<EventSubscription[]>(`/api/v1/api-access/subscriptions?appId=${id}`),
        apiGet<EventCatalogueEntry[]>("/api/v1/api-access/event-catalogue"),
      ])
    : [null, null, null, null, null];
  const roleList = roles?.ok ? roles.data : [];
  const t = await getTranslations("apiAccess.admin");
  return (
    <>
      <p>
        <Link href="/admin/api">{t("back")}</Link>
      </p>
      <h1>{detail.data.app.name}</h1>
      <AppDetailAdmin
        initial={detail.data}
        scopes={scopes.data}
        assignments={assignments?.ok ? assignments.data : []}
        roles={roleList}
        roleNames={await roleNames(roleList)}
        units={units?.ok ? units.data : []}
        subscriptions={subscriptions?.ok ? subscriptions.data : []}
        catalogue={catalogue?.ok ? catalogue.data : []}
      />
    </>
  );
}
