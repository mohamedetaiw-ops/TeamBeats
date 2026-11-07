import { request, type APIRequestContext } from "@playwright/test";
import { stack } from "./stack";

/**
 * Test data setup through the public API, as the seeded fictional platform operator (deploy/compose/seed). Calls go
 * to the API on loopback with x-forwarded-host, which is how the web app's backend-for-frontend reaches it, so the
 * API resolves the platform or tenant scope from the host exactly as it does for a browser.
 */
const operatorEmail = process.env.E2E_PLATFORM_OPERATOR ?? "operator@platform.example";

export type TestTenant = {
  id: string;
  key: string;
  defaultLocale: "ar" | "en";
  adminEmail: string;
};

function sessionFrom(setCookie: string | undefined): string {
  const token = /tb_session=([^;]+)/.exec(setCookie ?? "")?.[1];
  if (!token) {
    throw new Error("The development sign-in did not return a session cookie.");
  }
  return token;
}

async function platformClient(): Promise<APIRequestContext> {
  const host = `platform.${stack.baseDomain}`;
  const anonymous = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host } });
  const response = await anonymous.post("/dev/platform/signin", { data: { email: operatorEmail } });
  if (!response.ok()) {
    throw new Error(`Platform sign-in failed with ${response.status()}. Is the development host running with the seed file?`);
  }
  const token = sessionFrom(response.headers()["set-cookie"]);
  await anonymous.dispose();
  return request.newContext({
    baseURL: stack.apiOrigin,
    extraHTTPHeaders: { "x-forwarded-host": host, cookie: `tb_session=${token}` },
  });
}

/** Unique, valid tenant key (spec 001: 3 to 40 lowercase letters, digits and hyphens). */
export function uniqueKey(prefix: string): string {
  const suffix = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
  return `e2e-${prefix}-${suffix}`.slice(0, 40).replace(/-+$/, "");
}

/**
 * Creates a tenant with one appointed administrator. Tenants are never reused, so tests stay independent. The package
 * is Workplace unless a test needs a capability of another one, such as integration.microsoft365 in Workplace Plus.
 * otherAdmins are local parts of more appointed administrators, for tests that need several people in the tenant.
 */
export async function createTenant(prefix: string, defaultLocale: "ar" | "en", packageKey = "workplace", otherAdmins: string[] = []): Promise<TestTenant> {
  const platform = await platformClient();
  const key = uniqueKey(prefix);
  const adminEmail = `admin@${key}.example`;
  const response = await platform.post("/api/v1/platform/tenants", {
    data: {
      key,
      displayName: `${key} holding`,
      packageKey,
      defaultLocale,
      enabledLocales: ["ar", "en"],
      timeZone: "Asia/Dubai",
      admins: [adminEmail, ...otherAdmins.map((local) => `${local}@${key}.example`)],
    },
  });
  if (response.status() !== 201) {
    throw new Error(`Tenant creation failed with ${response.status()}: ${await response.text()}`);
  }
  const created = (await response.json()) as { id: string };
  await platform.dispose();
  return { id: created.id, key, defaultLocale, adminEmail };
}
