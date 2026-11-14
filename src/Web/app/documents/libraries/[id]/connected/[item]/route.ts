import { NextResponse, type NextRequest } from "next/server";
import { apiGet } from "@/lib/api";

/**
 * Where a search result or citation for a connected SharePoint file leads (spec 011 FR-027): the API checks the
 * library's audience and the mirrored readers, then the person goes on to the file in SharePoint, which checks again.
 * A route, not a page, because it never draws anything.
 */
export async function GET(request: NextRequest, { params }: { params: Promise<{ id: string; item: string }> }) {
  const { id, item } = await params;
  const link = await apiGet<{ webLink: string }>(
    `/api/v1/documents/libraries/${encodeURIComponent(id)}/connected/links/${encodeURIComponent(item)}`,
  );
  if (link.ok && link.data.webLink.startsWith("https://")) {
    return NextResponse.redirect(link.data.webLink);
  }
  return NextResponse.redirect(new URL(`/documents/libraries/${encodeURIComponent(id)}`, request.url));
}
