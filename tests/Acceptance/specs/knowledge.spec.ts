import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 011 T013 and T014 (US4): a knowledge editor starts an SOP article from its template, publishes it in the content
 * editor, and reads it in the knowledge base with its sections and contents; a reader answer of "helpful" shows in the
 * owner's score. In English and Arabic, with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`knowledge base in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const title = locale === "ar" ? "استقبال الزوار" : "Receiving visitors";

    test.beforeAll(async () => {
      tenant = await createTenant(`kb-${locale}`, locale, "workplace");
    });

    test("an editor writes an SOP article from the template and readers rate it", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/kb`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "documents.kb.title"));
      await expect(page.getByText(msg(locale, "documents.kb.none"))).toBeVisible();
      await expectAccessible(page, testInfo, `kb-list-empty-${locale}`);

      await page.getByRole("link", { name: msg(locale, "documents.kb.new") }).click();
      await page.waitForURL(/\/kb\/new$/);
      await page.getByRole("radio", { name: msg(locale, "documents.kb_new.templateValue.sop") }).check();
      await expect(page.getByTestId("template-sections")).toContainText(msg(locale, "documents.kb_new.section.responsibilities"));
      await expectAccessible(page, testInfo, `kb-new-${locale}`);
      await page.locator(`input[lang="${locale}"]`).fill(title);
      await page.getByRole("button", { name: msg(locale, "documents.kb_new.create") }).click();
      await page.waitForURL(/\/content\/items\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(title);
      const body = page.getByRole("textbox", { name: msg(locale, "content.items.body") });
      await expect(body.getByRole("heading", { name: msg(locale, "documents.kb_new.section.purpose") })).toBeVisible();
      await page.getByRole("button", { name: msg(locale, "content.items.publish") }).click();
      await expect(page.getByRole("main").getByRole("status")).toContainText(msg(locale, "content.items.published"));

      await page.goto(`${hostOrigin(tenant.key)}/kb`);
      await expect(page.getByTestId("article-list").getByText(msg(locale, "documents.kb.template.sop"))).toBeVisible();
      await expectAccessible(page, testInfo, `kb-list-${locale}`);
      await page.getByTestId("article-list").getByRole("link", { name: title }).click();
      await page.waitForURL(/\/kb\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(title);
      const contents = page.getByRole("navigation", { name: msg(locale, "documents.kb.contents") });
      await expect(contents.getByRole("link")).toHaveCount(5);
      await expect(page.getByRole("heading", { level: 2, name: msg(locale, "documents.kb_new.section.steps") })).toBeVisible();
      await expect(page.getByTestId("article-score")).toContainText(msg(locale, "documents.kb.noScore"));

      await page.getByRole("button", { name: msg(locale, "documents.feedback.yes") }).click();
      await page.getByLabel(msg(locale, "documents.feedback.comment")).fill(locale === "ar" ? "واضحة جدًا" : "Very clear");
      await page.getByRole("button", { name: msg(locale, "documents.feedback.send") }).click();
      await expect(page.getByTestId("feedback-saved")).toHaveText(msg(locale, "documents.feedback.thanks"));
      await expectAccessible(page, testInfo, `kb-article-${locale}`);
      await page.reload();
      await expect(page.getByTestId("article-score")).toContainText("100");
      await expect(page.getByRole("button", { name: msg(locale, "documents.feedback.update") })).toBeVisible();
    });
  });
}
