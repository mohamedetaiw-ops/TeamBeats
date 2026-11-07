import type { Page } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, referenceProps, saveAndPublish, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { stack } from "../support/stack";

/**
 * Spec 008 T015: what a keyboard or screen reader user meets on the shell and the home, and that everything stays
 * reachable on a phone. The skip link reaches the content; landmarks and menu controls carry names and states; at
 * 375px every shell control can be reached by Tab, is on screen when focused, and the page passes axe.
 */
test.skip(stack.target === "remote", "Pages use the reference component packs of the development host.");

const unique = (prefix: string) => `${prefix}-${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

async function seed(tenant: TestTenant) {
  const api = await tenantApi(tenant);
  const home = await createPage(api, "H01", unique("home"));
  await saveAndPublish(api, home, home.draft.regions.map((r) => ({
    ...r,
    placements: r.id === "hero"
      ? [{ id: "critical", componentRef: "reference.alpha.text@1", props: referenceProps["reference.alpha.text@1"] }]
      : [{ id: "services", componentRef: "reference.alpha.links@1", props: referenceProps["reference.alpha.links@1"] }],
  })));
  const node = async (body: Record<string, unknown>) => {
    const id = crypto.randomUUID();
    const response = await api.put(`/api/v1/experience/ia/${id}`, { data: body });
    expect(response.ok(), await response.text()).toBe(true);
    return id;
  };
  const base = unique("about");
  const about = await node({ labels: { en: "About us", ar: "من نحن" }, slug: base, url: `/${base}` });
  const team = await node({ labels: { en: "Our team", ar: "فريقنا" }, slug: `${base}/team`, url: `/${base}/team`, parentId: about });
  const menu = await api.put("/api/v1/experience/menus/global/tenant", { data: { items: [{ nodeId: about, children: [{ nodeId: team }] }] } });
  expect(menu.ok(), await menu.text()).toBe(true);
  const shell = await api.put("/api/v1/experience/shell/definition", {
    data: { start: ["identity", "global_navigation"], center: ["search"], end: ["settings"], below: ["breadcrumbs"], utilities: [], homePageId: home.id },
  });
  expect(shell.ok(), await shell.text()).toBe(true);
  await api.dispose();
}

/** Tabs through the page from the top and returns what took focus, until focus reaches the main content. */
async function tabUntilMain(page: Page, limit = 30) {
  const seen: { name: string; inView: boolean }[] = [];
  await page.reload();
  for (let i = 0; i < limit; i++) {
    await page.keyboard.press("Tab");
    const step = await page.evaluate(() => {
      const el = document.activeElement as HTMLElement | null;
      if (!el || el === document.body) return null;
      const box = el.getBoundingClientRect();
      return {
        inMain: !!el.closest("main"),
        name: (el.getAttribute("aria-label") ?? el.textContent ?? "").trim(),
        inView: box.width > 0 && box.height > 0 && box.left >= 0 && box.right <= window.innerWidth + 1,
      };
    });
    if (!step || step.inMain) break;
    seen.push({ name: step.name, inView: step.inView });
  }
  return seen;
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`shell reachability in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const about = locale === "en" ? "About us" : "من نحن";
    const team = locale === "en" ? "Our team" : "فريقنا";

    test.beforeAll(async () => {
      tenant = await createTenant(`reach-${locale}`, locale);
      await seed(tenant);
    });

    test("the skip link, landmarks and menu states serve keyboard and screen reader users", async ({ page }) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await expect(page.locator("main")).toContainText(locale === "en" ? "Wear a helmet on the plant floor." : "ارتد الخوذة في أرض المصنع.");

      // On a fresh load the first Tab reaches the skip link; following it, the next Tab lands in the content.
      await page.reload();
      await page.keyboard.press("Tab");
      const skip = page.getByRole("link", { name: msg(locale, "app.skipToContent") });
      await expect(skip).toBeFocused();
      await expect(skip).toBeInViewport();
      await page.keyboard.press("Enter");
      await page.keyboard.press("Tab");
      expect(await page.evaluate(() => !!document.activeElement?.closest("main"))).toBe(true);

      // One main landmark, named navigation, and the page title for screen readers.
      await expect(page.getByRole("main")).toHaveCount(1);
      await expect(page.getByRole("heading", { level: 1, name: msg(locale, "home.pageTitle") })).toBeAttached();
      const nav = page.getByRole("navigation", { name: msg(locale, "shell.globalNav") });
      await expect(nav).toBeVisible();

      // The submenu toggle says what it opens and whether it is open, and the hidden list is out of the reading order.
      const toggle = nav.getByRole("button", { name: msg(locale, "shell.submenu", { label: about }) });
      await expect(toggle).toHaveAttribute("aria-expanded", "false");
      const controls = await toggle.getAttribute("aria-controls");
      expect(controls).toBeTruthy();
      await expect(nav.getByRole("link", { name: team })).toHaveCount(0);
      await toggle.focus();
      await page.keyboard.press("Space");
      await expect(toggle).toHaveAttribute("aria-expanded", "true");
      await expect(page.locator(`[id="${controls}"]`).getByRole("link", { name: team })).toBeVisible();
      // Focus leaving the menu closes it.
      await page.keyboard.press("Tab");
      await page.keyboard.press("Tab");
      await expect(toggle).toHaveAttribute("aria-expanded", "false");

      // The personalization mode button is reachable and announces the mode in a live region.
      const start = page.getByRole("button", { name: msg(locale, "personalization.start") });
      await start.focus();
      await page.keyboard.press("Enter");
      await expect(page.locator("main [role=status]").first()).toHaveText(msg(locale, "personalization.modeOn"));
    });

    test("on a phone every shell control is reachable by Tab and on screen", async ({ page }, testInfo) => {
      await page.setViewportSize({ width: 375, height: 800 });
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await expect(page.locator("main")).toContainText(locale === "en" ? "Wear a helmet on the plant floor." : "ارتد الخوذة في أرض المصنع.");

      const steps = await tabUntilMain(page);
      expect(steps.length).toBeGreaterThan(2);
      expect(steps.filter((s) => !s.inView), JSON.stringify(steps)).toEqual([]);
      expect(steps.map((s) => s.name)).toContain(msg(locale, "shell.menu"));
      expect(steps.map((s) => s.name)).toContain(msg(locale, "shell.settings.open"));

      // The phone menu opens by keyboard and its links are reachable by Tab.
      const nav = page.getByRole("navigation", { name: msg(locale, "shell.globalNav") });
      const menu = nav.getByRole("button", { name: msg(locale, "shell.menu") });
      await menu.focus();
      await page.keyboard.press("Enter");
      await expect(menu).toHaveAttribute("aria-expanded", "true");
      await page.keyboard.press("Tab");
      await expect(nav.getByRole("link", { name: about })).toBeFocused();
      await expect(nav.getByRole("link", { name: about })).toBeInViewport();
      await page.keyboard.press("Escape");
      await expect(menu).toBeFocused();
      await expect(menu).toHaveAttribute("aria-expanded", "false");

      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow).toBeLessThanOrEqual(0);
      await expectAccessible(page, testInfo, `shell-phone-${locale}`);
    });
  });
}
