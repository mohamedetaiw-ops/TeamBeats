import type { Locator, Page } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 030 slice 4 (US4): a shared kiosk. A device administrator adds a kiosk with only some features and gets a
 * one-time code; the tablet is set up with it and offers badge and PIN sign-in; a first badge chooses its PIN; the
 * kiosk session reaches only the allowed features, also when a page of another feature is typed in; after the idle
 * time it signs out on its own and leaves nothing on the tablet. Every screen passes axe in both languages. The rules
 * themselves (lockout, unit scope, stolen device cookie, revoke, cross-tenant) are covered by KioskTests.
 */
const pin = "482916";

async function issueBadge(tenant: TestTenant): Promise<string> {
  const api = await tenantApi(tenant);
  const me = await api.get("/api/v1/me");
  expect(me.ok(), await me.text()).toBe(true);
  const { id } = (await me.json()) as { id: string };
  const issued = await api.post(`/api/v1/identity/badges/${id}`, { data: {} });
  expect(issued.ok(), await issued.text()).toBe(true);
  const { code } = (await issued.json()) as { code: string };
  await api.dispose();
  return code;
}

/** Everything the tablet keeps for a page or a person: browser storage and caches other than the app shell. */
async function leftOnDevice(page: Page): Promise<{ local: number; session: number; caches: string[] }> {
  return page.evaluate(async () => ({
    local: localStorage.length,
    session: sessionStorage.length,
    caches: (await caches.keys()).filter((k) => !k.startsWith("tb-shell-")),
  }));
}

/** Waits until React has taken over an element the server rendered, so its effects (such as a timer) are running. */
async function running(locator: Locator): Promise<void> {
  await expect
    .poll(() => locator.evaluate((el) => Object.keys(el).some((key) => key.startsWith("__reactProps"))), { timeout: 30_000 })
    .toBe(true);
}

/** An API call from the page itself, with the tablet's own cookies. */
async function call(page: Page, path: string): Promise<{ status: number; detailKey: string | undefined }> {
  return page.evaluate(async (url) => {
    const response = await fetch(url, { cache: "no-store" });
    const body = (await response.json().catch(() => ({}))) as { detailKey?: string };
    return { status: response.status, detailKey: response.ok ? undefined : body.detailKey };
  }, path);
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`shared kiosk in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    const m = (key: string, values?: Record<string, string>) => msg(locale, key, values);
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`kiosk-${locale}`, locale, "workplace_plus");
      // A kiosk belongs to an organization unit, and a new tenant has none yet.
      const api = await tenantApi(tenant);
      const unit = await api.post("/api/v1/org/units", {
        data: { parentId: null, typeKey: "department", code: "WAREHOUSE", names: { en: "Warehouse", ar: "المستودع" } },
      });
      expect(unit.ok(), await unit.text()).toBe(true);
      await api.dispose();
    });

    test("a kiosk is set up, a badge signs in with a new PIN, other features stay closed and idle signs out", async ({ page, browser }, testInfo) => {
      test.setTimeout(120_000);
      const origin = hostOrigin(tenant.key);

      // The device administrator adds a kiosk for leave only, open to every unit, signing out after one minute.
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/admin/kiosks`);
      await expect(page.getByRole("heading", { level: 1, name: m("kiosk.admin.title") })).toBeVisible();
      await expect(page.getByText(m("kiosk.admin.empty"))).toBeVisible();
      await page.getByRole("textbox", { name: m("kiosk.admin.name"), exact: true }).fill("Gate tablet");
      await page.getByRole("textbox", { name: m("kiosk.admin.location"), exact: true }).fill("Gate 2");
      for (const feature of ["announcements", "policies", "shifts", "services", "booking"]) {
        await page.getByLabel(m(`kiosk.admin.feature.${feature}`)).uncheck();
      }
      await page.getByLabel(m("kiosk.admin.idleMinutes")).fill("1");
      await page.getByLabel(m("kiosk.admin.allowOtherUnits")).check();
      await page.getByRole("button", { name: m("kiosk.admin.create") }).click();
      const code = (await page.locator(".kiosk-code").first().textContent())?.trim() ?? "";
      expect(code).toMatch(/^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$/);
      await expect(page.getByText(m("kiosk.admin.status.waiting"))).toBeVisible();
      await expectAccessible(page, testInfo, `kiosk-admin-${locale}`);
      const badge = await issueBadge(tenant);

      // The tablet: a browser with nobody signed in. The clock is controlled so the idle time passes at once.
      const tablet = await browser.newContext({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      const kiosk = await tablet.newPage();
      await kiosk.clock.install();
      await kiosk.goto(`${origin}/kiosk`);
      await expect(kiosk.getByRole("heading", { name: m("kiosk.enrol.title") })).toBeVisible();
      await expect(kiosk.locator("html")).toHaveAttribute("dir", locale === "ar" ? "rtl" : "ltr");
      await expectAccessible(kiosk, testInfo, `kiosk-enrol-${locale}`);
      await kiosk.getByLabel(m("kiosk.enrol.code")).fill(code.toLowerCase());
      await kiosk.getByRole("button", { name: m("kiosk.enrol.submit") }).click();
      await expect(kiosk.getByRole("heading", { name: m("kiosk.signIn.title") })).toBeVisible();
      await expect(kiosk.getByText(m("kiosk.signIn.deviceAt", { name: "Gate tablet", location: "Gate 2" }))).toBeVisible();
      await expectAccessible(kiosk, testInfo, `kiosk-signin-${locale}`);

      // A badge with no PIN yet chooses one, typed twice; two different PINs are refused.
      await kiosk.getByLabel(m("kiosk.signIn.badge")).fill(badge);
      await kiosk.getByLabel(m("kiosk.signIn.pin")).fill("0000");
      await kiosk.getByRole("button", { name: m("kiosk.signIn.submit") }).click();
      await expect(kiosk.getByRole("heading", { name: m("kiosk.signIn.chooseTitle") })).toBeVisible();
      await kiosk.getByLabel(m("kiosk.signIn.newPin")).fill(pin);
      await kiosk.getByLabel(m("kiosk.signIn.confirmPin")).fill("482917");
      await kiosk.getByRole("button", { name: m("kiosk.signIn.choose") }).click();
      await expect(kiosk.getByText(m("kiosk.signIn.pinMismatch"))).toBeVisible();
      await kiosk.getByLabel(m("kiosk.signIn.newPin")).fill(pin);
      await kiosk.getByLabel(m("kiosk.signIn.confirmPin")).fill(pin);
      await kiosk.getByRole("button", { name: m("kiosk.signIn.choose") }).click();
      await kiosk.waitForURL(`${origin}/`);
      await expect(kiosk.getByRole("region", { name: m("kiosk.session.label") })).toBeVisible();
      await expectAccessible(kiosk, testInfo, `kiosk-home-${locale}`);

      // Leave is open; payslips are not a kiosk feature, so typing the address is refused by the API.
      expect(await call(kiosk, "/api/v1/me/leave")).toEqual({ status: 200, detailKey: undefined });
      expect(await call(kiosk, "/api/v1/me/payslips")).toEqual({ status: 403, detailKey: "authz.denied.shared_device" });
      await kiosk.goto(`${origin}/leave`);
      await expect(kiosk.getByRole("heading", { level: 1, name: m("leave.title") })).toBeVisible();
      await kiosk.evaluate(() => {
        localStorage.setItem("left-behind", "1");
        sessionStorage.setItem("left-behind", "1");
      });

      // Without use, the warning comes 30 seconds before the end, then the kiosk signs out and keeps nothing.
      // The idle timer starts once the page is running in the browser, not when its HTML arrives.
      await running(kiosk.getByRole("region", { name: m("kiosk.session.label") }).getByRole("button"));
      await kiosk.clock.fastForward(31_000);
      await expect(kiosk.getByRole("dialog", { name: m("kiosk.session.warningTitle") })).toBeVisible();
      await kiosk.clock.fastForward(31_000);
      await kiosk.waitForURL(`${origin}/kiosk`);
      await expect(kiosk.getByRole("heading", { name: m("kiosk.signIn.title") })).toBeVisible();
      expect(await leftOnDevice(kiosk)).toEqual({ local: 0, session: 0, caches: [] });
      expect((await call(kiosk, "/api/v1/me/leave")).status).toBe(401);

      // The PIN chosen at the kiosk now signs in straight away.
      await kiosk.getByLabel(m("kiosk.signIn.badge")).fill(badge);
      await kiosk.getByLabel(m("kiosk.signIn.pin")).fill(pin);
      await kiosk.getByRole("button", { name: m("kiosk.signIn.submit") }).click();
      await kiosk.waitForURL(`${origin}/`);
      await kiosk.getByRole("button", { name: m("kiosk.session.signOut") }).click();
      await kiosk.waitForURL(`${origin}/kiosk`);
      await tablet.close();

      // Back on the administrator's screen the kiosk is ready.
      await page.reload();
      await expect(page.getByText(m("kiosk.admin.status.enrolled"))).toBeVisible();
    });
  });
}
