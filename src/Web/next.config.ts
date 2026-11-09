import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";

const apiOrigin = process.env.TEAMBEATS_API_ORIGIN ?? "http://localhost:5080";

const config: NextConfig = {
  output: "standalone",
  poweredByHeader: false,
  // The release in frontend error reports (spec 021 US9): the deployed commit, or "dev" locally.
  env: { NEXT_PUBLIC_TEAMBEATS_RELEASE: process.env.TEAMBEATS_RELEASE ?? "dev" },
  // One origin per tenant host (r0-solution-structure section 5). In deployed environments the ingress routes
  // /api and /dev to the API directly; these rewrites serve local development.
  async rewrites() {
    return [
      { source: "/api/:path*", destination: `${apiOrigin}/api/:path*` },
      { source: "/dev/:path*", destination: `${apiOrigin}/dev/:path*` },
    ];
  },
};

export default createNextIntlPlugin("./i18n/request.ts")(config);
