import Link from "next/link";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { Microsoft365Connection } from "@/lib/integrations/types";
import { Microsoft365Detail } from "@/features/connections/Microsoft365Detail";
import { connectionsSection } from "@/features/connections/guard";
import "@/features/connections/connections.css";

/** One Microsoft 365 connection: features, permissions, consent and options (spec 006 US5, spec 017 T005). */
export default async function Microsoft365ConnectionPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  await connectionsSection();
  const connection = await apiGet<Microsoft365Connection>(`/api/v1/integrations/connections/${encodeURIComponent(id)}`);
  if (!connection.ok) {
    notFound();
  }
  const t = await getTranslations("connections");
  return (
    <>
      <p>
        <Link href="/admin/connections">{t("back")}</Link>
      </p>
      <h1>
        <bdi>{connection.data.displayName}</bdi>
      </h1>
      <Microsoft365Detail initial={connection.data} />
    </>
  );
}
