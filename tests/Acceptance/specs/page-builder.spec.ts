import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 032 T018: the Pages section lists the editor's pages, and a new page is created from the template
 * chooser, whose templates carry a text description and their regions. Works in English and Arabic, with no critical
 * axe findings. Template names come from the API's own catalogue, never from the test.
 */
function apiText(locale: LaunchLocale, key: string): string {
  const file = fileURLToPath(new URL(`../../../src/BuildingBlocks/Localization/catalogues/${locale}.json`, import.meta.url));
  const value = (JSON.parse(readFileSync(file, "utf8")) as Record<string, string>)[key];
  if (!value) throw new Error(`Missing API message ${locale}:${key}`);
  return value;
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`page builder in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });

    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`builder-${locale}`, locale);
    });

    test("creates a page from the template chooser and lists it", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/admin`);
      await page.getByRole("link", { name: msg(locale, "admin.nav.pages") }).click();
      await page.waitForURL(`${origin}/admin/pages`);
      await expect(page.getByRole("heading", { level: 1, name: msg(locale, "builder.pages.title") })).toBeVisible();
      await expect(page.getByText(msg(locale, "builder.pages.empty"))).toBeVisible();
      await expectAccessible(page, testInfo, `pages-empty-${locale}`);

      await page.getByRole("link", { name: msg(locale, "builder.pages.create") }).click();
      await page.waitForURL(`${origin}/admin/pages/new`);
      const overview = page.getByRole("radio", { name: apiText(locale, "experience.template.t04") });
      await overview.check();
      // The chooser says in text what the preview shows.
      const card = page.locator("label.builder-template").filter({ has: overview });
      await expect(card).toContainText(apiText(locale, "experience.template.t04.description"));
      await expect(card).toContainText(msg(locale, "builder.regions.hero"));
      await expectAccessible(page, testInfo, `pages-new-${locale}`);

      const slug = page.getByLabel(msg(locale, "builder.create.slug"));
      await slug.fill("Not A Slug");
      await page.getByRole("button", { name: msg(locale, "builder.create.submit") }).click();
      await expect(page.locator(".field-error")).toHaveText(msg(locale, "builder.create.slugInvalid"));

      await slug.fill(`news/${locale}-overview`);
      await page.getByRole("button", { name: msg(locale, "builder.create.submit") }).click();
      await page.waitForURL(/\/admin\/pages\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(`/p/news/${locale}-overview`);
      await expect(page.getByText(msg(locale, "builder.pages.draftOnly"))).toBeVisible();
      await expect(page.locator("section.builder-region:not(.builder-publish):not(.builder-panel)")).toHaveCount(2);
      await expectAccessible(page, testInfo, `pages-detail-${locale}`);

      // The same address again is refused with the reason next to the field.
      await page.goto(`${origin}/admin/pages/new`);
      await page.getByLabel(msg(locale, "builder.create.slug")).fill(`news/${locale}-overview`);
      await page.getByRole("button", { name: msg(locale, "builder.create.submit") }).click();
      await expect(page.locator(".field-error")).toHaveText(msg(locale, "builder.create.slugTaken"));

      await page.goto(`${origin}/admin/pages`);
      const row = page.getByTestId("pages-table").getByRole("row").filter({ hasText: `/p/news/${locale}-overview` });
      await expect(row).toContainText(apiText(locale, "experience.template.t04"));
      await expect(row).toContainText(msg(locale, "builder.pages.wholeTenant"));
    });

    test("composes a page with the keyboard, undoes and redoes, survives offline and conflicts, and publishes", async ({ page, context }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      const api = await tenantApi(tenant);
      const slug = `team/${locale}-editor`;
      const created = await createPage(api, "T03", slug);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/admin/pages/${created.id}`);

      const saveState = page.getByTestId("save-state");
      const announcer = page.getByTestId("announcer");
      const main = msg(locale, "builder.regions.main");
      const aside = msg(locale, "builder.regions.aside");
      const textName = apiText(locale, "experience.component.reference_alpha_text.name");
      const kpiName = apiText(locale, "experience.component.reference_alpha_kpi.name");
      await expect(saveState).toHaveText(msg(locale, "builder.editor.save.saved"));

      // Add a text block to Main through the picker; its settings open, drawn from the schema.
      await page.getByRole("button", { name: msg(locale, "builder.editor.add", { region: main }) }).click();
      const picker = page.getByRole("dialog");
      await expect(picker).toBeVisible();
      await expectAccessible(page, testInfo, `editor-picker-${locale}`);
      await picker.getByRole("button", { name: msg(locale, "builder.picker.addLabel", { name: textName }) }).click();
      await expect(picker).toBeHidden();
      const text = page.getByTestId("placement-text-1");
      await expect(text).toBeFocused();
      await expect(announcer).toHaveText(msg(locale, "builder.editor.added", { name: textName, region: main }));
      const body = apiText(locale, "experience.component.reference_alpha_text.props.body");
      const bodyGroup = text.getByRole("group", { name: body });
      await bodyGroup.locator('[lang="en"]').fill("Wear a helmet on the plant floor.");
      await bodyGroup.locator('[lang="ar"]').fill("ارتد الخوذة في أرض المصنع.");
      await expect(saveState).toHaveText(msg(locale, "builder.editor.save.saved"));

      // A second component, then keyboard moves: focus stays on the moved card and the move is announced.
      await page.getByRole("button", { name: msg(locale, "builder.editor.add", { region: main }) }).click();
      await page.getByRole("dialog").getByRole("button", { name: msg(locale, "builder.picker.addLabel", { name: kpiName }) }).click();
      const kpi = page.getByTestId("placement-kpi-1");
      await kpi.getByRole("group").first().locator('[lang="en"]').fill("Uptime");
      await expect(saveState).toHaveText(msg(locale, "builder.editor.save.saved"));
      // Translation status per language: the figure's label exists in English only.
      const languageName = (code: string) => page.evaluate(([ui, c]) => new Intl.DisplayNames([ui], { type: "language" }).of(c) ?? c, [locale, code] as const);
      await expect(page.getByTestId("translation-en")).toHaveText(msg(locale, "builder.editor.checks.complete", { language: await languageName("en") }));
      await expect(page.getByTestId("translation-ar")).not.toHaveText(msg(locale, "builder.editor.checks.complete", { language: await languageName("ar") }));

      // Previews by device and direction show the saved draft in a frame.
      await page.getByRole("button", { name: msg(locale, "builder.editor.preview.devices.phone") }).click();
      await page.getByLabel(msg(locale, "builder.editor.preview.language")).selectOption("ar");
      const frame = page.frameLocator('[data-testid="preview-frame"]');
      await expect(frame.locator("main")).toHaveAttribute("dir", "rtl");
      await expect(frame.getByText("ارتد الخوذة في أرض المصنع.")).toBeVisible();
      await page.getByLabel(msg(locale, "builder.editor.preview.language")).selectOption("en");
      await expect(frame.locator("main")).toHaveAttribute("dir", "ltr");
      await expect(frame.getByText("Wear a helmet on the plant floor.")).toBeVisible();

      await text.focus();
      await page.keyboard.press("Alt+ArrowDown");
      await expect(text).toBeFocused();
      await expect(announcer).toHaveText(msg(locale, "builder.editor.moved", { name: textName, number: "2", count: "2", region: main }));
      const order = () => page.locator(".builder-placement").evaluateAll((els) => els.map((e) => e.getAttribute("data-testid")));
      expect(await order()).toEqual(["placement-kpi-1", "placement-text-1"]);
      await expectAccessible(page, testInfo, `editor-${locale}`);

      // Undo and redo replay in order.
      await page.keyboard.press("Control+z");
      await expect.poll(order).toEqual(["placement-text-1", "placement-kpi-1"]);
      await page.keyboard.press("Control+Shift+z");
      await expect.poll(order).toEqual(["placement-kpi-1", "placement-text-1"]);
      await page.getByRole("button", { name: msg(locale, "builder.editor.undo"), exact: true }).click();
      await expect.poll(order).toEqual(["placement-text-1", "placement-kpi-1"]);

      // The action menu does what the keys do: move to another region, duplicate, remove.
      await text.getByRole("button", { name: msg(locale, "builder.editor.actionsFor", { name: textName }) }).click();
      await text.getByRole("button", { name: msg(locale, "builder.editor.moveTo", { region: aside }) }).click();
      await expect(page.getByRole("region", { name: aside }).getByTestId("placement-text-1")).toBeVisible();
      await expect(text).toBeFocused();
      await page.keyboard.press("Alt+d");
      await expect(page.getByTestId("placement-text-2")).toBeFocused();
      await page.keyboard.press("Delete");
      await expect(page.getByTestId("placement-text-2")).toHaveCount(0);
      await expect(saveState).toHaveText(msg(locale, "builder.editor.save.saved"));

      // Offline: the edit is kept in this browser and saves once back online.
      await context.setOffline(true);
      await kpi.focus();
      await page.keyboard.press("Alt+d");
      await expect(saveState).toHaveText(msg(locale, "builder.editor.save.offline"));
      const kept = await page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith("tb.builder.")).length);
      expect(kept).toBe(1);
      await context.setOffline(false);
      await expect(saveState).toHaveText(msg(locale, "builder.editor.save.saved"));

      // Someone else saves first: both versions are kept until the editor chooses.
      const current = await api.get(`/api/v1/experience/pages/${created.id}`);
      const view = (await current.json()) as { eTag: string; draft: Record<string, unknown> };
      expect((await api.put(`/api/v1/experience/pages/${created.id}/draft`, { data: { definition: view.draft }, headers: { "if-match": view.eTag } })).ok()).toBe(true);
      await page.getByTestId("placement-kpi-2").focus();
      await page.keyboard.press("Delete");
      await expect(saveState).toHaveText(msg(locale, "builder.editor.save.conflict"));
      await page.getByRole("button", { name: msg(locale, "builder.editor.keepMine") }).click();
      await expect(saveState).toHaveText(msg(locale, "builder.editor.save.saved"));

      // Publish, and the page renders for employees.
      await page.getByRole("button", { name: msg(locale, "builder.editor.publish") }).click();
      await expect(announcer).toHaveText(msg(locale, "builder.editor.publishedNow", { release: "1" }));
      await page.goto(`${origin}/p/${slug}`);
      await expect(page.getByText(locale === "ar" ? "ارتد الخوذة في أرض المصنع." : "Wear a helmet on the plant floor.")).toBeVisible();
      await api.dispose();
    });
  });
}
