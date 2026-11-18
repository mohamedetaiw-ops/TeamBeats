import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { seedHome } from "../support/home";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * The v2 home as staging seeds it (src/Api.DevHost/seeds/staging-home.json): the file's placements are published on a
 * fresh tenant, which also checks the file against the component manifests, and the home shows the task summary and My
 * services of spec 016 under the briefing, next to featured news, with the campaign band at the bottom (the news, services and menu the seeder
 * also publishes are covered by tests/Integration/StagingHomeSeedTests.cs), in English and Arabic with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`v2 home in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`v2home-${locale}`, locale, "workplace_plus");
      await seedHome(tenant);
    });

    test("the seeded home shows the briefing, your day, services, Ask TeamBeats and the campaign band last", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/`);
      const day = page.getByTestId("home-your-day");
      await expect(day.getByRole("heading", { name: msg(locale, "home.day.title") })).toBeVisible();
      // The briefing beside it already says how many things wait, so Your day shows no counts here (DLS-102).
      await expect(day.getByText(msg(locale, "home.day.toApprove"))).toHaveCount(0);
      // DLS-100: the briefing and the top of Your day are in the first viewport on a desktop and on a phone.
      for (const size of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
        await page.setViewportSize(size);
        await page.evaluate(() => window.scrollTo(0, 0));
        const briefingBox = await page.getByTestId("home-briefing").boundingBox();
        const dayBox = await day.boundingBox();
        expect(briefingBox!.y, `briefing top at ${size.width}px`).toBeLessThan(size.height / 2);
        // 64 px is kept for the tab bar a phone shows at the bottom.
        expect(dayBox!.y, `Your day top at ${size.width}px`).toBeLessThan(size.height - 64);
      }
      await page.setViewportSize({ width: 1280, height: 720 });
      await expect(day.getByRole("link", { name: msg(locale, "home.day.inbox") })).toHaveAttribute("href", "/tasks");
      // DLS-101: every home widget carries the content budget of its region, and no list shows more rows than it.
      await expect(day).toHaveAttribute("data-budget", /^[358]$/);
      const over = await page.evaluate(() =>
        [...document.querySelectorAll<HTMLElement>(".ui-widget[data-budget]")].filter((w) =>
          [...w.querySelectorAll(":is(.ui-widget-list, .home-list, .outlook-list, .people-list, .documents-list, .content-list, .ui-cal-agenda)")].some(
            (list) => [...list.children].filter((li) => getComputedStyle(li).display !== "none").length > Number(w.dataset.budget),
          ),
        ).length,
      );
      expect(over, "widgets showing more rows than their budget").toBe(0);
      // This tenant has no Microsoft 365, so the agenda says so in one line instead of guessing meetings.
      await expect(day.getByText(msg(locale, "home.day.calendarUnavailable"))).toBeVisible();
      // Ask TeamBeats opens the assistant panel beside the page (spec 014 FR-001).
      await expect(page.locator(".shell-bar").getByRole("button", { name: msg(locale, "shell.ask") })).toHaveAttribute("aria-haspopup", "dialog");
      const services = page.getByTestId("home-my-services");
      await expect(services.getByRole("heading", { name: msg(locale, "services.home.services.title") })).toBeVisible();
      await expect(services.getByRole("link").first()).toBeVisible();
      const briefing = page.getByTestId("home-briefing");
      await expect(briefing.getByRole("heading", { level: 2 })).toBeVisible();
      await expect(briefing.getByText(msg(locale, "home.briefing.sources"))).toBeVisible();
      // The campaign band closes the page: after the news and services, never above them.
      const band = page.getByRole("link", { name: locale === "ar" ? "اطلع على البرنامج" : "See the programme" });
      // seedHome links the band to the home, as the test tenant has no seeded story (support/home.ts).
      await expect(band).toHaveAttribute("href", "/");
      const bandTop = (await band.boundingBox())!.y;
      expect(bandTop).toBeGreaterThan((await services.boundingBox())!.y);
      await expectAccessible(page, testInfo, `home-v2-${locale}`);
      await page.screenshot({ path: testInfo.outputPath(`home-v2-${locale}.png`), fullPage: true });
    });

    test("with 30 tasks and 20 meetings the home stays inside its content budget", async ({ page }) => {
      // Spec 036 DLS-101 and DLS-118: the high-volume scenario. The viewer's own sources answer with a busy day; the
      // home must not grow with them.
      const at = (minutes: number) => new Date(Date.now() + minutes * 60_000).toISOString();
      const tasks = Array.from({ length: 30 }, (_, i) => ({
        taskId: `00000000-0000-4000-8000-${String(i).padStart(12, "0")}`, source: "service_request", name: `Request ${i + 1}`, step: null,
        requestId: null, number: null, serviceId: null, serviceNames: null, requester: null, requestedFor: null, onBehalfOf: null,
        createdAt: at(-60 * (i + 1)), dueAt: at(i % 2 === 0 ? -30 : 600), title: { en: `Request ${i + 1}`, ar: `طلب ${i + 1}` }, link: "/tasks",
      }));
      const meetings = Array.from({ length: 20 }, (_, i) => ({
        id: `m${i}`, subject: `Meeting ${i + 1}`, start: at(10 + i * 20), end: at(25 + i * 20), isAllDay: false, isPrivate: false,
        location: null, webLink: "https://outlook.office.com/calendar/fake", source: "outlook",
      }));
      await page.route("**/api/v1/tasks", (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(tasks) }));
      await page.route("**/api/v1/me/calendar?*", (route) => route.fulfill({
        status: 200, contentType: "application/json",
        body: JSON.stringify({ state: "ok", asOf: at(0), timeZone: "Asia/Dubai", from: at(-60), to: at(1440), items: meetings }),
      }));
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/`);
      const day = page.getByTestId("home-your-day");
      const budget = Number(await day.getAttribute("data-budget"));
      // The agenda shows at most the budget of its region and ends in View all with the total.
      await expect(day.locator(".ui-cal-agenda > li")).toHaveCount(Math.min(budget, 4));
      await expect(day.locator("[data-widget-more]")).toContainText(/20|٢٠/);
      await expect(page.getByTestId("home-briefing").locator(".home-briefing-row")).toHaveCount(3);
      for (const [size, viewports] of [[{ width: 1440, height: 900 }, 2.5], [{ width: 390, height: 844 }, 6]] as const) {
        await page.setViewportSize(size);
        const height = await page.evaluate(() => document.documentElement.scrollHeight);
        expect(height, `home height at ${size.width}px`).toBeLessThanOrEqual(size.height * viewports);
      }
    });
  });
}

const paragraph = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });

async function publishNews(api: APIRequestContext, en: string, ar: string) {
  const created = await api.post("/api/v1/content/items", {
    data: { type: "news", variants: { en: { title: en, body: paragraph(en) }, ar: { title: ar, body: paragraph(ar) } } },
  });
  expect(created.status(), await created.text()).toBe(201);
  const id = ((await created.json()) as { id: string }).id;
  const published = await api.post(`/api/v1/content/items/${id}/publish`, { data: {} });
  expect(published.ok(), await published.text()).toBe(true);
}

test.describe("v2 home language switch", () => {
  test.use({ locale: "en-US" });
  let tenant: TestTenant;

  test.beforeAll(async () => {
    tenant = await createTenant("v2home-switch", "en", "workplace_plus");
    await seedHome(tenant);
    const api = await tenantApi(tenant);
    await publishNews(api, "Shuttle times change on Monday", "تتغير مواعيد الحافلة يوم الاثنين");
    await api.dispose();
  });

  test("switching language shows the news in the new language without a reload", async ({ page }) => {
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.goto(`${hostOrigin(tenant.key)}/`);
    await expect(page.getByRole("link", { name: "Shuttle times change on Monday" })).toBeVisible();
    await page.getByRole("button", { name: msg("en", "shell.settings.open") }).click();
    await page.getByLabel(msg("en", "shell.settings.language")).selectOption("ar");
    await expect(page.getByRole("link", { name: "تتغير مواعيد الحافلة يوم الاثنين" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Shuttle times change on Monday" })).toHaveCount(0);
  });
});
