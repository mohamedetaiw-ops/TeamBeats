import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { getTokenSchema, listBrands } from "@/lib/brand/client";
import { TenantDesigns, type TenantDesign } from "@/features/brand/TenantDesigns";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { ButtonLink } from "@/components/ui/Button";
import "@/features/brand/brand.css";

/**
 * Tenant designs (spec 036 R3, DLS-120 to DLS-122): designs derived from one of the four, their review and applying one to
 * a brand's draft. The page shows what the API answers for this person and decides nothing about access.
 */
export default async function TenantDesignsPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "branding")) {
    notFound();
  }
  const [list, schema, brands] = await Promise.all([
    apiGet<{ available: boolean; limit: number; items: TenantDesign[] }>("/api/v1/experience/designs"),
    getTokenSchema(),
    listBrands(),
  ]);
  if (!list.ok || !schema.ok) {
    notFound();
  }
  const t = await getTranslations("brand.designs");
  return (
    <>
      <AdminHeader
        section="branding"
        title={t("title")}
        description={t("intro")}
        actions={
          <ButtonLink href="/admin/branding" variant="secondary" icon="chevron-back">
            {t("back")}
          </ButtonLink>
        }
      />
      <TenantDesigns
        initial={list.data.items}
        available={list.data.available}
        limit={list.data.limit}
        schema={schema.data.tokens}
        brands={brands.ok ? brands.data.map((b) => ({ id: b.id, name: b.name })) : []}
      />
    </>
  );
}
