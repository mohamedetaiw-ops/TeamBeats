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

    test("up to 1024px the global menu sits behind one disclosure button, and above it the links show (spec 008 FR-018)", async ({ page }) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const nav = page.getByRole("navigation", { name: msg(locale, "shell.globalNav") });
      const button = nav.getByRole("button", { name: msg(locale, "shell.menu"), exact: true });
      const link = nav.getByRole("link", { name: about(), exact: true });
      for (const width of [700, 900, 1024]) {
        await page.setViewportSize({ width, height: 800 });
        await expect(button, `disclosure at ${width}px`).toBeVisible();
        await expect(button).toHaveAttribute("aria-expanded", "false");
        await expect(link, `menu closed at ${width}px`).toBeHidden();
        await button.click();
        await expect(button).toHaveAttribute("aria-expanded", "true");
        await expect(link).toBeVisible();
        await page.keyboard.press("Escape");
        await expect(button).toHaveAttribute("aria-expanded", "false");
        await expect(button).toBeFocused();
        await noHorizontalScroll(page);
      }
      await page.setViewportSize({ width: 1025, height: 800 });
      await expect(button).toBeHidden();
      await expect(link).toBeVisible();
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
      // Before anything is typed it offers the commands the person may use (spec 008 FR-008); typing filters them.
      const commands = palette.getByRole("group", { name: msg(locale, "shell.search.palette.commands") });
      await expect(commands.getByRole("option", { name: msg(locale, "shell.search.commands.ask_assistant") })).toBeVisible();
      await box.fill(msg(locale, "shell.search.commands.ask_assistant"));
      await expect(commands.getByRole("option", { name: msg(locale, "shell.search.commands.ask_assistant") })).toBeVisible();
      await expect(commands.getByRole("option", { name: msg(locale, "shell.search.commands.my_tasks") })).toHaveCount(0);
      await box.fill(setup.teamSlug.split("/")[0]!);
      await expect(palette.getByRole("option", { name: about() })).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(palette).toBeHidden();
    });

    test("a page is kept as a favorite, and favorites and recent visits open from the palette (spec 008 FR-017)", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/${setup.teamSlug}`);
      await page.getByRole("button", { name: msg(locale, "shell.favorites.button") }).click();
      const menu = page.getByRole("dialog", { name: msg(locale, "shell.favorites.title") });
      await expect(menu).toBeVisible();
      await menu.getByRole("button", { name: msg(locale, "shell.favorites.addHere") }).click();
      await expect(menu.getByRole("status").filter({ hasText: msg(locale, "shell.favorites.added") })).toBeVisible();
      await expect(menu.getByRole("link", { name: team() })).toHaveAttribute("href", `/${setup.teamSlug}`);
      await expect(menu.getByRole("button", { name: msg(locale, "shell.favorites.removeHere") })).toBeVisible();
      await expectAccessible(page, testInfo, `shell-favorites-${locale}`);
      await page.keyboard.press("Escape");
      await expect(menu).toBeHidden();

      // The visit is recorded once the page has settled.
      await expect
        .poll(async () => ((await page.evaluate(async (u) => (await fetch(u, { cache: "no-store" })).json(), "/api/v1/me/recents")) as { path: string }[]).map((r) => r.path))
        .toContain(`/${setup.teamSlug}`);
      await page.goto(`${hostOrigin(tenant.key)}/`);
      await expect(page.getByRole("searchbox", { name: msg(locale, "shell.search.label") })).toBeVisible();
      await page.keyboard.press("Control+K");
      const palette = page.getByRole("dialog", { name: msg(locale, "shell.search.palette.label") });
      await expect(palette.getByRole("group", { name: msg(locale, "shell.search.palette.favorites") }).getByRole("option", { name: team() })).toBeVisible();
      const recents = palette.getByRole("group", { name: msg(locale, "shell.search.palette.recents") });
      await expect(recents.getByRole("option", { name: new RegExp(setup.teamSlug) })).toBeVisible();
      await recents.getByRole("option", { name: msg(locale, "shell.search.palette.clearRecents") }).click();
      await expect
        .poll(async () => ((await page.evaluate(async (u) => (await fetch(u, { cache: "no-store" })).json(), "/api/v1/me/recents")) as unknown[]).length)
        .toBe(0);

      // Removing it from the page leaves no favorite behind.
      await page.goto(`${hostOrigin(tenant.key)}/${setup.teamSlug}`);
      await page.getByRole("button", { name: msg(locale, "shell.favorites.button") }).click();
      await menu.getByRole("button", { name: msg(locale, "shell.favorites.removeHere") }).click();
      await expect(menu.getByRole("status").filter({ hasText: msg(locale, "shell.favorites.removed") })).toBeVisible();
      await expect(menu.getByText(msg(locale, "shell.favorites.empty"))).toBeVisible();
    });

    test("focus mode hides the side areas at once and is kept for this device (spec 008 FR-009)", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/${setup.teamSlug}`);
      const toggle = page.getByRole("button", { name: msg(locale, "shell.focus.label") });
      await expect(toggle).toHaveAttribute("aria-pressed", "false");
      await toggle.click();
      await expect(toggle).toHaveAttribute("aria-pressed", "true");
      await expect(page.locator("html")).toHaveAttribute("data-focus", "on");
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "shell.focus.on") })).toBeAttached();
      await expect(page.locator(".shell-section")).toBeHidden();
      await expect(page.getByRole("main")).toBeVisible();
      await expectAccessible(page, testInfo, `shell-focus-${locale}`);
      // The choice is saved, so it holds after a reload; turning it off brings the side areas back.
      await expect
        .poll(async () => ((await page.evaluate(async (u) => (await fetch(u, { cache: "no-store" })).json(), "/api/v1/me/preference-profile")) as { values: Record<string, string> }).values.focus)
        .toBe("on");
      await page.reload();
      await expect(page.locator("html")).toHaveAttribute("data-focus", "on");
      await page.getByRole("button", { name: msg(locale, "shell.focus.label") }).click();
      await expect(page.locator("html")).not.toHaveAttribute("data-focus", "on");
      await expect
        .poll(async () => ((await page.evaluate(async (u) => (await fetch(u, { cache: "no-store" })).json(), "/api/v1/me/preference-profile")) as { values: Record<string, string> }).values.focus)
        .toBe("off");
    });

    test("a hint and a short tour written for a page show below the bar, can be closed and reopened from the help menu (spec 008 FR-020)", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const hintTitle = bilingual("Ask your team", "اسأل فريقك");
      const published = await api.put("/api/v1/experience/help", {
        data: {
          items: [
            { key: `hint-${locale}`, kind: "hint", path: `/${setup.teamSlug}`, title: hintTitle, body: bilingual("Your team lead answers questions here.", "يجيب قائد فريقك عن الأسئلة هنا."), steps: null },
            {
              key: `tour-${locale}`, kind: "tour", path: `/${setup.teamSlug}`, title: bilingual("Team tour", "جولة الفريق"), body: null,
              steps: [
                { title: bilingual("Who we are", "من نحن"), body: bilingual("Meet the people on the team.", "تعرف على أعضاء الفريق.") },
                { title: bilingual("How to reach us", "كيف تصل إلينا"), body: bilingual("Use the help desk link.", "استخدم رابط مكتب المساعدة.") },
              ],
            },
          ],
        },
      });
      expect(published.ok(), await published.text()).toBe(true);
      await api.dispose();

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/${setup.teamSlug}`);
      const hint = page.locator(".shell-help-area").getByText(hintTitle[locale]);
      await expect(hint).toBeVisible();
      await expectAccessible(page, testInfo, `shell-help-${locale}`);
      await page.locator(".shell-help-area").getByRole("button", { name: msg(locale, "shell.help.dismiss") }).click();
      await expect(hint).toHaveCount(0);

      // The help menu lists both and starts the tour, one step at a time, in the page flow.
      await page.getByRole("button", { name: msg(locale, "shell.help.button") }).click();
      const menu = page.getByRole("dialog", { name: msg(locale, "shell.help.title") });
      await menu.getByRole("button", { name: new RegExp(locale === "en" ? "Team tour" : "جولة الفريق") }).click();
      const tour = page.getByRole("region", { name: locale === "en" ? "Who we are" : "من نحن" });
      await expect(tour).toBeVisible();
      await expect(page.getByRole("heading", { name: locale === "en" ? "Who we are" : "من نحن" })).toBeFocused();
      await expectAccessible(page, testInfo, `shell-tour-${locale}`);
      await tour.getByRole("button", { name: msg(locale, "shell.help.next") }).click();
      const last = page.getByRole("region", { name: locale === "en" ? "How to reach us" : "كيف تصل إلينا" });
      await last.getByRole("button", { name: msg(locale, "shell.help.finish") }).click();
      await expect(last).toHaveCount(0);
      await expect(page.getByRole("button", { name: msg(locale, "shell.help.button") })).toBeFocused();

      // A dismissed hint comes back from the menu.
      await page.getByRole("button", { name: msg(locale, "shell.help.button") }).click();
      await menu.getByRole("button", { name: new RegExp(hintTitle[locale]) }).click();
      await expect(hint).toBeVisible();
    });

    test("only one shell panel is open at a time, and a press outside closes it (spec 008 FR-060)", async ({ page }) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const settings = page.getByTestId("shell-settings-panel");
      const account = page.getByTestId("shell-account-panel");
      await page.getByRole("button", { name: msg(locale, "shell.settings.open") }).click();
      await expect(settings).toBeVisible();
      await page.getByTestId("shell-account").click();
      await expect(account).toBeVisible();
      await expect(settings).toBeHidden();
      await page.getByRole("button", { name: msg(locale, "shell.settings.open") }).click();
      await expect(settings).toBeVisible();
      await expect(account).toBeHidden();
      // A press well away from the bar, below where any panel reaches, in either direction of writing.
      const size = page.viewportSize()!;
      await page.mouse.click(size.width / 2, size.height - 20);
      await expect(settings).toBeHidden();
    });

    test("settings are in one place and apply to the page; the menu stays reachable on a phone", async ({ page }) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.getByRole("button", { name: msg(locale, "shell.settings.open") }).click();
      await page.getByRole("radiogroup", { name: msg(locale, "shell.settings.density.label") }).getByRole("radio", { name: msg(locale, "shell.settings.density.compact") }).check();
      await expect(page.locator("html")).toHaveAttribute("data-density", "compact");
      await page.getByRole("switch", { name: msg(locale, "shell.settings.reduceMotion") }).focus();
      await page.keyboard.press("Space");
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
      // On a phone the global menu opens from More in the bottom navigation, pages under an item included (spec 008 FR-063).
      const nav = page.getByRole("navigation", { name: msg(locale, "app.nav.label"), exact: true });
      const panel = nav.locator(".mobile-nav-more");
      await expect(panel.getByRole("link", { name: team(), exact: true })).toBeHidden();
      await nav.getByRole("button", { name: msg(locale, "app.nav.more"), exact: true }).click();
      await expect(panel.getByRole("link", { name: team(), exact: true })).toBeVisible();
      await noHorizontalScroll(page);
    });
  });
}
