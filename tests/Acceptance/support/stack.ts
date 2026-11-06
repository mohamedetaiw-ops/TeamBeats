/**
 * Where the suite runs. Defaults match the local quick start in the repository README.
 *   E2E_TARGET       "local" (start the stack) or "remote" (use a deployed environment, see docs/testing/e2e-plan.md)
 *   E2E_BASE_DOMAIN  tenant hosts are <key>.<base domain>; the platform console is platform.<base domain>
 *   E2E_WEB_PORT     port of the web app on every tenant host (empty for the default port of the scheme)
 *   E2E_SCHEME       http or https
 *   E2E_API_ORIGIN   API origin the local stack listens on; tests reach it on loopback with x-forwarded-host
 */
const target = process.env.E2E_TARGET === "remote" ? "remote" : "local";

export const stack = {
  target,
  baseDomain: process.env.E2E_BASE_DOMAIN ?? "teambeats.localhost",
  webPort: process.env.E2E_WEB_PORT ?? (target === "local" ? "3000" : ""),
  scheme: process.env.E2E_SCHEME ?? (target === "local" ? "http" : "https"),
  apiOrigin: process.env.E2E_API_ORIGIN ?? "http://localhost:5080",
} as const;

/** Origin of a tenant (or the platform console) as a browser sees it. */
export function hostOrigin(subdomain: string): string {
  const port = stack.webPort ? `:${stack.webPort}` : "";
  return `${stack.scheme}://${subdomain}.${stack.baseDomain}${port}`;
}
