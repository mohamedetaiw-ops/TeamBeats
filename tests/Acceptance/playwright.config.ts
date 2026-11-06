import { defineConfig, devices } from "@playwright/test";
import { stack } from "./support/stack";

/**
 * End-to-end acceptance suite (r0-solution-structure section 7, spec 007 T013 and T014).
 *
 * Local target (default): Playwright starts the development API host and the Next.js app, against the PostgreSQL
 * database configured in src/Api.DevHost/appsettings.Development.json. Already running servers are reused outside CI.
 * Remote target (E2E_TARGET=remote): no servers are started; E2E_BASE_DOMAIN and E2E_WEB_PORT point at the deployed
 * environment. See tests/Acceptance/README.md and docs/testing/e2e-plan.md.
 */
const isCI = !!process.env.CI;
const local = stack.target === "local";

export default defineConfig({
  testDir: "./specs",
  fullyParallel: true,
  forbidOnly: isCI,
  retries: isCI ? 1 : 0,
  workers: isCI ? 2 : undefined,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: isCI ? [["github"], ["html", { open: "never" }], ["list"]] : [["list"], ["html", { open: "never" }]],
  use: {
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "off",
  },
  projects: [
    {
      name: "chromium",
      use: {
        ...devices["Desktop Chrome"],
        // Every tenant is a subdomain of the base domain; map them all to this machine for the local target.
        launchOptions: local ? { args: [`--host-resolver-rules=MAP *.${stack.baseDomain} 127.0.0.1`] } : {},
      },
    },
  ],
  webServer: local
    ? [
        {
          name: "api",
          command: `dotnet run --project ../../src/Api.DevHost --no-launch-profile --urls ${stack.apiOrigin}`,
          url: `${stack.apiOrigin}/health/live`,
          reuseExistingServer: !isCI,
          timeout: 180_000,
          stdout: "ignore",
          env: {
            ASPNETCORE_ENVIRONMENT: "Development",
            // The web app and these tests reach the API as a proxy on loopback and pass the tenant host on.
            Http__TrustForwardedHeaders: "true",
          },
        },
        {
          name: "web",
          command: "npm run dev -- --port " + stack.webPort,
          cwd: "../../src/Web",
          url: `http://localhost:${stack.webPort}`,
          reuseExistingServer: !isCI,
          timeout: 180_000,
          env: {
            TEAMBEATS_API_ORIGIN: stack.apiOrigin,
            TEAMBEATS_DEV_SIGNIN: "true",
            NEXT_TELEMETRY_DISABLED: "1",
          },
        },
      ]
    : undefined,
});
