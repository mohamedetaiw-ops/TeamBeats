import type { APIRequestContext, Page } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, referenceProps, saveAndPublish, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 008 T011 with part of T015: the shell frame from the published definition, the global menu by keyboard (Escape
 * and focus leaving close it), the same menu behind one button at mobile width, IA addresses with breadcrumbs and
 * section menus, old slug redirects, the one recovery answer, quick navigation, the settings panel in one place with
 * preferences applied to the page, in English and Arabic with axe.
 */
test.skip(stack.target === "remote", "Pages use the reference component packs of the development host.");

const bilingual = (en: string, ar: string) => ({ en, ar });
const unique = (prefix: string) => `${prefix}-${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

async function node(api: APIRequestContext, body: Record<string, unknown>): Promise<string> {
  const id = crypto.randomUUID();
  const response = await api.put(`/api/v1/experience/ia/${id}`, { data: body });
  if (!response.ok()) {
    throw new Error(`IA entry failed with ${response.status()}: ${await response.text()}`);
  }
  return id;
}

type Setup = { about: string; team: string; teamSlug: string; oldSlug: string; pageSlug: string };

async function seed(tenant: TestTenant): Promise<Setup> {
  const api = await tenantApi(tenant);
  const page = await createPage(api, "T01", unique("team"));
  await saveAndPublish(api, page, page.draft.regions.map((r) => ({
    ...r,
    placements: r.id === "main" ? [{ id: "intro", componentRef: "reference.alpha.text@1", props: referenceProps["reference.alpha.text@1"] }] : [],
  })));
  const base = unique("about");
  const about = await node(api, { labels: bilingual("About us", "من نحن"), slug: base, url: `/${base}/team` });
  const oldSlug = `${base}/staff`;
  const team = await node(api, { labels: bilingual("Our team", "فريقنا"), slug: oldSlug, pageId: page.id, parentId: about });
  // Renaming keeps the old address as a redirect.
  const teamSlug = `${base}/team`;
  const renamed = await api.put(`/api/v1/experience/ia/${team}`, { data: { labels: bilingual("Our team", "فريقنا"), slug: teamSlug, pageId: page.id, parentId: about } });
  expect(renamed.ok()).toBe(true);
  const help = await node(api, { labels: bilingual("Help desk", "مكتب المساعدة"), slug: unique("help"), url: "https://example.org/help" });
  const menu = await api.put("/api/v1/experience/menus/global/tenant", {
    data: { items: [{ nodeId: about, children: [{ nodeId: team }, { nodeId: help }] }, { nodeId: help }] },
  });
  expect(menu.ok()).toBe(true);
  const section = await api.put(`/api/v1/experience/menus/section/${about}`, { data: { items: [{ nodeId: team }, { nodeId: help }] } });
  expect(section.ok()).toBe(true);
  await api.dispose();
  return { about, team, teamSlug, oldSlug, pageSlug: page.slug };
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`shell in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let setup: Setup;
    const about = () => (locale === "en" ? "About us" : "من نحن");
    const team = () => (locale === "en" ? "Our team" : "فريقنا");

    test.beforeAll(async () => {
      tenant = await createTenant(`shell-${locale}`, locale);
      setup = await seed(tenant);
    });

    test("the global menu works by keyboard and closes on Escape and when focus leaves", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const nav = page.getByRole("navigation", { name: msg(locale, "shell.globalNav") });
      await expect(nav.getByRole("link", { name: about() })).toBeVisible();
      const toggle = nav.getByRole("button", { name: msg(locale, "shell.submenu", { label: about() }) });
      await toggle.focus();
      await page.keyboard.press("Enter");
      await expect(toggle).toHaveAttribute("aria-expanded", "true");
      await page.keyboard.press("Tab");
      await expect(nav.getByRole("link", { name: team() })).toBeFocused();
      await page.keyboard.press("Escape");
      await expect(toggle).toHaveAttribute("aria-expanded", "false");
      await expect(toggle).toBeFocused();
      await page.keyboard.press("Enter");
      await page.getByRole("heading", { level: 1 }).click();
      await expect(toggle).toHaveAttribute("aria-expanded", "false");
      await expectAccessible(page, testInfo, `shell-${locale}`);
    });

    test("IA addresses show breadcrumbs and the section menu, old slugs redirect, and unknown addresses recover", async ({ page }) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/${setup.teamSlug}`);
      const crumbs = page.getByRole("navigation", { name: msg(locale, "shell.breadcrumbs") });
      await expect(crumbs.getByRole("link")).toHaveText([about(), team()]);
      await expect(crumbs.getByRole("link", { name: team() })).toHaveAttribute("aria-current", "page");
      await expect(page.getByRole("navigation", { name: msg(locale, "shell.sectionNav") }).getByRole("link")).toHaveCount(2);
      await expect(page.locator("main")).toContainText(locale === "en" ? "Wear a helmet on the plant floor." : "ارتد الخوذة في أرض المصنع.");

      await page.goto(`${hostOrigin(tenant.key)}/${setup.oldSlug}`);
      await page.waitForURL(`${hostOrigin(tenant.key)}/${setup.teamSlug}`);

      await page.goto(`${hostOrigin(tenant.key)}/${unique("missing")}`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "shell.recovery.title"));
      await expect(page.getByRole("link", { name: msg(locale, "shell.recovery.home") })).toBeVisible();
    });

    test("quick navigation lists permitted destinations", async ({ page }) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.getByRole("searchbox", { name: msg(locale, "shell.search.label") }).fill(setup.teamSlug.split("/")[0]!);
      await expect(page.locator(".shell-search-results").getByRole("link", { name: about() })).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(page.locator(".shell-search-results")).toHaveCount(0);
    });

    test("the search shortcut opens the command palette with the same permitted destinations", async ({ page }) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await expect(page.getByRole("searchbox", { name: msg(locale, "shell.search.label") })).toBeVisible();
      await page.keyboard.press("Control+K");
      const palette = page.getByRole("dialog", { name: msg(locale, "shell.search.palette.label") });
      await expect(palette).toBeVisible();
      const box = palette.getByRole("combobox");
      await expect(box).toBeFocused();
      await expect(palette.getByRole("status")).toHaveText(msg(locale, "shell.search.palette.hint"));
      await box.fill(setup.teamSlug.split("/")[0]!);
      await expect(palette.getByRole("option", { name: about() })).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(palette).toBeHidden();
    });

    test("only one shell panel is open at a time, and a press outside closes it (spec 008 FR-060)", async ({ page }) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const settings = page.locator(".shell-settings-panel");
      const account = page.locator(".shell-account-panel");
      await page.getByRole("button", { name: msg(locale, "shell.settings.open") }).click();
      await expect(settings).toBeVisible();
      await page.locator(".shell-avatar").click();
      await expect(account).toBeVisible();
      await expect(settings).toBeHidden();
      await page.getByRole("button", { name: msg(locale, "shell.settings.open") }).click();
      await expect(settings).toBeVisible();
      await expect(account).toBeHidden();
      await page.locator("main").click({ position: { x: 60, y: 60 } });
      await expect(settings).toBeHidden();
    });

    test("settings are in one place and apply to the page; the menu stays reachable on a phone", async ({ page }) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.getByRole("button", { name: msg(locale, "shell.settings.open") }).click();
      await page.getByLabel(msg(locale, "shell.settings.density.label")).selectOption("compact");
      await expect(page.locator("html")).toHaveAttribute("data-density", "compact");
      await page.getByLabel(msg(locale, "shell.settings.motion.label")).selectOption("reduced");
      await expect(page.locator("html")).toHaveAttribute("data-motion", "reduced");
      await page.getByLabel(msg(locale, "shell.settings.text_scale.label")).selectOption("200");
      await expect(page.locator("html")).toHaveAttribute("style", /font-size: 200%/);
      await page.keyboard.press("Escape");
      await expect(page.getByRole("button", { name: msg(locale, "shell.settings.open") })).toBeFocused();

      // The choices follow the user to the next page load.
      await page.reload();
      await expect(page.locator("html")).toHaveAttribute("data-density", "compact");

      await page.setViewportSize({ width: 375, height: 800 });
      await page.getByRole("button", { name: msg(locale, "shell.settings.open") }).click();
      await page.getByLabel(msg(locale, "shell.settings.text_scale.label")).selectOption("100");
      await expect(page.locator("html")).not.toHaveAttribute("style", /font-size/);
      await page.keyboard.press("Escape");
      const nav = page.getByRole("navigation", { name: msg(locale, "shell.globalNav") });
      await expect(nav.getByRole("link", { name: about() })).toBeHidden();
      await nav.getByRole("button", { name: msg(locale, "shell.menu") }).click();
      await expect(nav.getByRole("link", { name: about() })).toBeVisible();
      await noHorizontalScroll(page);
    });
  });
}
