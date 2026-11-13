import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";

const apiOrigin = process.env.TEAMBEATS_API_ORIGIN ?? "http://localhost:5080";

const config: NextConfig = {
  output: "standalone",
  poweredByHeader: false,
  // The development badge sits over the bottom navigation on phones; build errors still show in the overlay.
  devIndicators: false,
  // The release in frontend error reports (spec 021 US9): the deployed commit, or "dev" locally.
  env: { NEXT_PUBLIC_TEAMBEATS_RELEASE: process.env.TEAMBEATS_RELEASE ?? "dev" },
  // One origin per tenant host (r0-solution-structure section 5). In deployed environments the ingress routes
  // /api, /dev, /oauth and /mcp to the API directly; these rewrites serve local development.
  // The service worker (spec 030 FR-002) is fetched fresh every time, so a new release is found at once.
  async headers() {
    return [
      {
        source: "/sw.js",
        headers: [
          { key: "Cache-Control", value: "no-cache, no-store, must-revalidate" },
          { key: "Content-Type", value: "application/javascript; charset=utf-8" },
        ],
      },
    ];
  },
  async rewrites() {
    return [
      { source: "/api/:path*", destination: `${apiOrigin}/api/:path*` },
      { source: "/dev/:path*", destination: `${apiOrigin}/dev/:path*` },
      // The tenant's authorization server for apps and AI clients (spec 035 FR-001) is served by the API.
      { source: "/oauth/:path*", destination: `${apiOrigin}/oauth/:path*` },
      { source: "/.well-known/oauth-authorization-server/:path*", destination: `${apiOrigin}/.well-known/oauth-authorization-server/:path*` },
      // The tenant's MCP server for external AI clients and its protected resource metadata (spec 035 FR-019).
      { source: "/mcp", destination: `${apiOrigin}/mcp` },
      // The public API's own OpenAPI document (spec 035 FR-014), linked from the developer page. The internal one is not served here.
      { source: "/openapi/public-v1.json", destination: `${apiOrigin}/openapi/public-v1.json` },
      { source: "/.well-known/oauth-protected-resource/:path*", destination: `${apiOrigin}/.well-known/oauth-protected-resource/:path*` },
    ];
  },
};

export default createNextIntlPlugin("./i18n/request.ts")(config);
