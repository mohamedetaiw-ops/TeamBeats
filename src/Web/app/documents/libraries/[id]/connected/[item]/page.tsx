import { notFound, redirect } from "next/navigation";
import { apiGet, type Me } from "@/lib/api";

/**
 * Where a search result or citation for a connected SharePoint file leads (spec 011 FR-027): the API checks the
 * library's audience and the mirrored readers, then the person goes on to the file in SharePoint, which checks again.
 */
export default async function ConnectedItemPage({ params }: { params: Promise<{ id: string; item: string }> }) {
  const { id, item } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const link = await apiGet<{ webLink: string }>(
    `/api/v1/documents/libraries/${encodeURIComponent(id)}/connected/links/${encodeURIComponent(item)}`,
  );
  if (!link.ok || !link.data.webLink.startsWith("https://")) {
    notFound();
  }
  redirect(link.data.webLink);
}
