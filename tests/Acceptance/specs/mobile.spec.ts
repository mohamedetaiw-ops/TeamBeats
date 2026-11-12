import type { Page } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 030 slice 1 (US1, US5): the installable app and phone layouts. On a 360 px phone the bottom navigation shows the
 * first entries with labels and "More" for the rest, mirrored in Arabic, with targets of at least 44 by 44; every R1 and
 * R2 page opens with no horizontal page scroll in both languages; the service worker installs, a new version waits for
 * Reload and never shows while a field has focus, and with no connection the offline page answers. The manifest per
 * tenant and brand, the icon and the navigation rules are covered by AppInstallTests and ShellNavigationTests.
 */
const phone = { width: 360, height: 740 };

/** Every page of R1 and R2 that opens without an id. Development tools, the gallery and the sign-in pages are left out. */
const routes = [
  "/", "/services", "/requests", "/requests/team", "/tasks", "/people", "/people/me", "/people/team", "/my-details", "/documents", "/kb",
  "/kb/new", "/search", "/notifications", "/notifications/preferences", "/settings", "/assistant", "/agents", "/calendar", "/calendar/manage",
  "/policies", "/policies/my", "/policies/new", "/content/items", "/content/items/new", "/content/health", "/leave", "/leave/team",
  "/attendance", "/attendance/team", "/letters", "/payslips", "/expenses", "/hr/journeys", "/communities", "/communities/awards",
  "/communities/celebrations", "/communities/moderation", "/communities/recognition", "/communities/surveys", "/learning",
  "/learning/catalogue", "/learning/manage", "/learning/reports", "/learning/team", "/spaces", "/spaces/manage", "/spaces/new", "/booking",
  "/booking/bookings", "/booking/manage", "/booking/reports", "/admin", "/admin/tenant", "/admin/users", "/admin/roles", "/admin/organization",
  "/admin/organization/directory", "/admin/identity", "/admin/plan", "/admin/branding", "/admin/shell", "/admin/pages", "/admin/pages/new",
  "/admin/templates", "/admin/people", "/admin/services", "/admin/notifications", "/admin/search", "/admin/connections",
  "/admin/connections/new", "/admin/calendars", "/admin/security", "/admin/security/alerts", "/admin/security/posture", "/admin/audit",
  "/admin/analytics", "/admin/analytics/errors", "/admin/ai", "/admin/ai/assistant", "/admin/ai/routing", "/admin/ai/usage", "/admin/agents",
  "/admin/agents/policy", "/admin/agents/runs", "/admin/evaluation", "/admin/evaluation/settings", "/admin/leave", "/admin/attendance",
  "/admin/letters", "/admin/expenses", "/admin/journeys", "/admin/data-changes", "/admin/hr-integration", "/learning/certificates/verify",
  "/letters/verify", "/offline",
];

/** Groups of pages, one test each, so the suite spreads over the shards. */
const groups = Array.from({ length: Math.ceil(routes.length / 12) }, (_, i) => routes.slice(i * 12, i * 12 + 12));

/** Menu entries for the bottom navigation: six, so four show and "More" holds two. */
const entries = [
  { url: "/services", en: "Services", ar: "الخدمات" },
  { url: "/people", en: "People", ar: "الأشخاص" },
  { url: "/documents", en: "Documents", ar: "المستندات" },
  { url: "/booking", en: "Booking", ar: "الحجز" },
  { url: "/learning", en: "Learning", ar: "التعلم" },
  { url: "/policies", en: "Policies", ar: "السياسات" },
];

async function seedMenu(tenant: TestTenant) {
  const api = await tenantApi(tenant);
  const items = [];
  for (const entry of entries) {
    const id = crypto.randomUUID();
    const slug = `m-${entry.url.slice(1)}-${Math.random().toString(36).slice(2, 8)}`;
    const node = await api.put(`/api/v1/experience/ia/${id}`, { data: { labels: { en: entry.en, ar: entry.ar }, slug, url: entry.url } });
    expect(node.ok(), await node.text()).toBe(true);
    items.push({ nodeId: id });
  }
  const menu = await api.put("/api/v1/experience/menus/global/tenant", { data: { items } });
  expect(menu.ok(), await menu.text()).toBe(true);
  await api.dispose();
}

/** The page is no wider than the window: nothing makes the whole page scroll sideways (FR-003). */
async function overflow(page: Page): Promise<number> {
  return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`mobile app in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string) => msg(locale, key);

    test.beforeAll(async () => {
      tenant = await createTenant(`mob-${locale}`, locale, "workplace_plus");
      await seedMenu(tenant);
    });

    test("the bottom navigation shows four entries and More, mirrored and easy to tap", async ({ page }, testInfo) => {
      await page.setViewportSize(phone);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const nav = page.getByRole("navigation", { name: m("app.nav.label"), exact: true });
      await expect(nav).toBeVisible();
      const bar = nav.locator(".mobile-nav-bar > li");
      await expect(bar).toHaveCount(5);
      for (const entry of entries.slice(0, 4)) {
        await expect(nav.getByRole("link", { name: entry[locale], exact: true })).toBeVisible();
      }
      for (const target of await nav.locator(".mobile-nav-bar a, .mobile-nav-bar button").all()) {
        const box = (await target.boundingBox())!;
        expect(box.width).toBeGreaterThanOrEqual(44);
        expect(box.height).toBeGreaterThanOrEqual(44);
      }
      // The first entry sits at the start edge: the left in English and the right in Arabic.
      const first = (await nav.getByRole("link", { name: entries[0]![locale], exact: true }).boundingBox())!;
      if (locale === "ar") expect(first.x + first.width).toBeGreaterThan(phone.width - 80);
      else expect(first.x).toBeLessThan(80);

      const more = nav.getByRole("button", { name: m("app.nav.more"), exact: true });
      await expect(more).toHaveAttribute("aria-expanded", "false");
      await more.click();
      await expect(more).toHaveAttribute("aria-expanded", "true");
      for (const entry of entries.slice(4)) {
        await expect(nav.getByRole("link", { name: entry[locale], exact: true })).toBeVisible();
      }
      await expectAccessible(page, testInfo, `bottom navigation ${locale}`);
      await page.keyboard.press("Escape");
      await expect(more).toHaveAttribute("aria-expanded", "false");
      await expect(more).toBeFocused();

      await nav.getByRole("link", { name: entries[1]![locale], exact: true }).click();
      await expect(page).toHaveURL(/\/people$/);
      await expect(nav.getByRole("link", { name: entries[1]![locale], exact: true })).toHaveAttribute("aria-current", "page");
    });

    groups.forEach((group, i) => {
      test(`pages ${i + 1} of ${groups.length} fit a 360 px phone`, async ({ page }) => {
        test.slow();
        await page.setViewportSize(phone);
        await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
        const wide: string[] = [];
        for (const route of group) {
          await page.goto(`${hostOrigin(tenant.key)}${route}`);
          await expect(page.locator("main").first()).toBeVisible();
          await page.waitForLoadState("load");
          await page.evaluate(() => document.fonts.ready);
          const extra = await overflow(page);
          if (extra > 1) wide.push(`${route} (${extra}px)`);
        }
        expect(wide, "pages wider than the phone").toEqual([]);
      });
    });
  });
}

test.describe("installed app", () => {
  let tenant: TestTenant;
  test.beforeAll(async () => {
    tenant = await createTenant("sw-en", "en", "workplace_plus");
    await seedMenu(tenant);
  });

  test("the experience administrator chooses the bottom navigation entries", async ({ page }) => {
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.goto(`${hostOrigin(tenant.key)}/admin/shell`);
    const section = page.getByRole("region", { name: msg("en", "shell_settings.mobile.title") });
    await expect(section.getByText(msg("en", "shell_settings.mobile.default"))).toBeVisible();
    await section.getByLabel("Policies", { exact: true }).check();
    await section.getByLabel("People", { exact: true }).check();
    await page.getByRole("button", { name: msg("en", "shell_settings.save"), exact: true }).click();
    await expect(page.getByText(msg("en", "shell_settings.saved"), { exact: true })).toBeVisible();

    await page.setViewportSize(phone);
    await page.goto(`${hostOrigin(tenant.key)}/`);
    const bar = page.getByRole("navigation", { name: msg("en", "app.nav.label"), exact: true }).locator(".mobile-nav-bar");
    // In the order of the global menu, then More for the other four.
    await expect(bar.getByRole("link")).toHaveText(["People", "Policies"]);
    await expect(bar.getByRole("button", { name: msg("en", "app.nav.more"), exact: true })).toBeVisible();
  });

  test("a new version waits for Reload and never shows while a field has focus", async ({ page }) => {
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.evaluate(async () => {
      await navigator.serviceWorker.ready;
      if (!navigator.serviceWorker.controller) await new Promise((r) => navigator.serviceWorker.addEventListener("controllerchange", r, { once: true }));
    });
    const prompt = page.getByRole("status", { name: msg("en", "app.update.title") });
    await expect(prompt).toHaveCount(0);

    // The person is typing when the next release arrives.
    const search = page.locator("input[type=search]").first();
    await search.click();
    await search.fill("leave");
    await page.evaluate(() => navigator.serviceWorker.register("/sw.js?v=e2e-next", { scope: "/" }));
    await page.waitForFunction(async () => !!(await navigator.serviceWorker.getRegistration())?.waiting);
    await expect(prompt).toHaveCount(0);

    await search.blur();
    await expect(prompt).toBeVisible();
    await prompt.getByRole("button", { name: msg("en", "app.update.later"), exact: true }).click();
    await expect(prompt).toHaveCount(0);
    // The release after that is offered again, and Reload applies it and reloads the page.
    await page.evaluate(() => navigator.serviceWorker.register("/sw.js?v=e2e-third", { scope: "/" }));
    await expect(prompt).toBeVisible();
    await Promise.all([page.waitForEvent("load"), prompt.getByRole("button", { name: msg("en", "app.update.reload"), exact: true }).click()]);
    const active = await page.evaluate(async () => (await navigator.serviceWorker.getRegistration())?.active?.scriptURL ?? "");
    expect(active).toContain("v=e2e-third");
  });

  test("with no connection a page opens the offline page", async ({ page, context }) => {
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.evaluate(async () => {
      await navigator.serviceWorker.ready;
      if (!navigator.serviceWorker.controller) await new Promise((r) => navigator.serviceWorker.addEventListener("controllerchange", r, { once: true }));
    });
    await context.setOffline(true);
    await page.goto(`${hostOrigin(tenant.key)}/people`).catch(() => undefined);
    await expect(page.getByRole("main").getByText(msg("en", "app.offline.title"), { exact: true })).toBeVisible();
    await context.setOffline(false);
  });
});
