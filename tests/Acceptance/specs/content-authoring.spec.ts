import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 009 T022 and T023: an editor starts a news item, writes the body in the block editor with the keyboard (a
 * heading, bold text and a list), saves, previews at phone width and publishes; a reader opens it, reacts and comments.
 * In English and Arabic, with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`content authoring in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const title = locale === "ar" ? "افتتاح المقصف الجديد" : "New canteen opens";
    const lead = locale === "ar" ? "يسعدنا الإعلان" : "We are glad to announce";
    const point = locale === "ar" ? "يفتح يوم الأحد" : "Opens on Sunday";

    test.beforeAll(async () => {
      tenant = await createTenant(`cnt-${locale}`, locale, "workplace");
    });

    test("an editor writes, previews and publishes an item that a reader then opens", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/content/items/new`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "content.items.new"));
      await expectAccessible(page, testInfo, `content-new-${locale}`);
      await page.getByLabel(msg(locale, "content.items.title")).fill(title);
      await page.getByRole("button", { name: msg(locale, "content.items.create") }).click();
      await page.waitForURL(/\/content\/items\/[0-9a-f-]{36}$/);
      const itemUrl = page.url();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(title);

      // The body, written with the keyboard and the toolbar.
      const body = page.getByRole("textbox", { name: msg(locale, "content.items.body") });
      await body.click();
      await page.keyboard.type(lead);
      await page.keyboard.press("Enter");
      const toolbar = page.getByRole("toolbar", { name: msg(locale, "content.editor.toolbar", { label: msg(locale, "content.items.body") }) });
      await toolbar.getByRole("button", { name: msg(locale, "content.editor.bulleted") }).click();
      await expect(toolbar.getByRole("button", { name: msg(locale, "content.editor.bulleted") })).toHaveAttribute("aria-pressed", "true");
      await toolbar.getByRole("button", { name: msg(locale, "content.editor.bold") }).click();
      await page.keyboard.type(point);
      await expect(body.locator("li strong, li .content-editor-bold").first()).toHaveText(point);
      await expectAccessible(page, testInfo, `content-editor-${locale}`);

      await page.getByLabel(msg(locale, "content.items.summary")).fill(lead);
      await page.getByRole("button", { name: msg(locale, "content.items.save") }).click();
      await expect(page.getByRole("main").getByRole("status")).toContainText(msg(locale, "content.items.saved"));

      // Preview at phone width shows what readers will get.
      await page.getByRole("button", { name: msg(locale, "content.items.deviceValue.phone") }).click();
      const preview = page.getByRole("article", { name: msg(locale, "content.items.previewOf", { title }) });
      await expect(preview).toHaveAttribute("data-device", "phone");
      await expect(preview.getByRole("listitem")).toHaveText(point);

      await page.getByRole("button", { name: msg(locale, "content.items.publish") }).click();
      await expect(page.getByRole("main").getByRole("status")).toContainText(msg(locale, "content.items.published"));
      await expect(page.getByTestId("item-status")).toContainText(msg(locale, "content.items.status.published"));

      // Opening the item as a reader reports a view (spec 021).
      const viewed = page.waitForRequest((r) => r.method() === "POST" && r.url().endsWith("/api/v1/analytics/events"));
      await page.getByRole("link", { name: msg(locale, "content.items.openReader") }).click();
      await page.waitForURL(/\/content\/read\//);
      expect((await viewed).postDataJSON()).toMatchObject({ events: [{ name: "content.viewed", fields: { source: "content" } }] });
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(title);
      await expect(page.getByRole("article").getByRole("listitem")).toHaveText(point);
      await expect(page.getByRole("article").locator("strong")).toHaveText(point);

      await page.getByRole("button", { name: new RegExp(`^${msg(locale, "content.social.kind.like")}`) }).click();
      await expect(page.getByRole("button", { name: new RegExp(`^${msg(locale, "content.social.kind.like")}`) })).toHaveAttribute("aria-pressed", "true");
      await page.getByLabel(msg(locale, "content.social.write")).fill(lead);
      await page.getByRole("button", { name: msg(locale, "content.social.send") }).click();
      await expect(page.getByText(lead, { exact: true }).last()).toBeVisible();
      await expectAccessible(page, testInfo, `content-reader-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/content/items`);
      await expect(page.getByRole("link", { name: title })).toBeVisible();
      await expectAccessible(page, testInfo, `content-list-${locale}`);

      // Spec 021 US1: the item's analytics, with the one-person audience shown only inside "other".
      await page.goto(itemUrl);
      await page.getByRole("link", { name: msg(locale, "content.items.analytics") }).click();
      await page.waitForURL(/\/analytics$/);
      await expect(page.getByTestId("item-analytics")).toBeVisible();
      await expect(page.getByTestId("analytics-audience")).toHaveText(/^(1|١)$/);
      await expect(page.getByTestId("analytics-groups").locator("tbody tr")).toHaveCount(1);
      await expect(page.getByTestId("analytics-groups").locator("tbody tr")).toHaveAttribute("data-group", "other");
      await expectAccessible(page, testInfo, `content-analytics-${locale}`);
    });
  });
}
