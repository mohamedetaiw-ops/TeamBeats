/**
 * The release this web server runs (spec 030 FR-002). An open page compares it with the release it was built from and
 * offers a reload only when they differ. It names a commit and nothing about the person or the tenant.
 */
export const dynamic = "force-dynamic";

export function GET() {
  return Response.json(
    { release: process.env.NEXT_PUBLIC_TEAMBEATS_RELEASE ?? "dev" },
    { headers: { "Cache-Control": "no-store" } },
  );
}
