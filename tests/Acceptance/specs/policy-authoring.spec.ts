import type { Page } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 025 T011 (second part): a compliance officer creates a policy and writes two clauses in both languages; a second
 * compliance officer opens the approval from the inbox, comments on a clause and approves it; the first launches the
 * attestation campaign and follows it in the compliance view down to one person. In English and Arabic, with axe on
 * every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`policy authoring in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const names = { en: "Information security", ar: "أمن المعلومات" };
    const clauses = [
      { en: ["Scope", "This policy covers every system."], ar: ["النطاق", "تشمل هذه السياسة كل الأنظمة."] },
      { en: ["Passwords", "Use a password manager."], ar: ["كلمات المرور", "استخدم مدير كلمات المرور."] },
    ];

    test.beforeAll(async () => {
      tenant = await createTenant(`pa-${locale}`, locale, "workplace", ["rana"]);
    });

    async function writeIn(page: Page, language: "en" | "ar") {
      await page.getByRole("tab", { name: m(`policies.editor.language.${language}`) }).click();
      const panel = page.getByRole("tabpanel");
      await panel.getByLabel(m("policies.editor.title"), { exact: true }).fill(names[language]);
      for (const [i, c] of clauses.entries()) {
        const position = String(i + 1);
        await panel.getByLabel(m("policies.editor.clauseTitle", { position })).fill(c[language][0]);
        const body = panel.getByRole("textbox", { name: m("policies.editor.clauseText", { position }) });
        await body.click();
        await page.keyboard.type(c[language][1]);
      }
    }

    test("create, write, review, approve, launch and follow a policy", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/policies`);
      await page.getByRole("link", { name: m("policies.register.create") }).click();
      await page.waitForURL(/\/policies\/new$/);
      await expectAccessible(page, testInfo, `policy-new-${locale}`);
      await page.getByLabel(m("policies.create.code")).fill("ISP-01");
      await page.getByLabel(m("policies.create.name", { language: m("policies.create.language.en") })).fill(names.en);
      await page.getByLabel(m("policies.create.name", { language: m("policies.create.language.ar") })).fill(names.ar);
      await page.getByRole("button", { name: m("policies.create.submit") }).click();
      await page.waitForURL(/\/policies\/[0-9a-f-]{36}\/edit$/);

      // Two clauses in both languages, the second one level deeper.
      await page.getByRole("button", { name: m("policies.editor.add") }).click();
      await page.getByLabel(m("policies.editor.level")).nth(1).selectOption("2");
      await writeIn(page, locale);
      await writeIn(page, locale === "en" ? "ar" : "en");
      await page.getByRole("tab", { name: m(`policies.editor.language.${locale}`) }).click();
      await expectAccessible(page, testInfo, `policy-editor-${locale}`);
      await page.getByRole("button", { name: m("policies.editor.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("policies.editor.saved") })).toBeVisible();
      await page.getByRole("button", { name: m("policies.editor.submit") }).click();
      await page.waitForURL(/\/policies\/[0-9a-f-]{36}$/);
      const policyUrl = page.url();
      await expect(page.getByRole("link", { name: m("policies.manage.inReview") })).toBeVisible();

      // The second compliance officer reviews from the inbox.
      await signInWithDevForm(page, tenant.key, `rana@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/tasks`);
      await page.getByTestId("task-inbox").getByRole("link", { name: names[locale] }).click();
      await page.waitForURL(/\/review$/);
      await expect(page.getByTestId("review-clause")).toHaveCount(2);
      await expect(page.getByTestId("review-clause").first()).toContainText(m("policies.review.change.added"));
      await page.getByLabel(m("policies.review.comment", { number: "1" }), { exact: true }).fill(locale === "ar" ? "واضح." : "Clear.");
      await page.getByRole("button", { name: m("policies.review.addComment") }).first().click();
      await expect(page.getByTestId("review-clause").first().getByText(locale === "ar" ? "واضح." : "Clear.")).toBeVisible();
      await expectAccessible(page, testInfo, `policy-review-${locale}`);
      await page.getByRole("button", { name: m("services.decision.approve") }).click();
      await page.waitForURL(/\/tasks$/);

      // The author launches the campaign and follows it.
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(policyUrl);
      await expect(page.getByTestId("policy-document").getByRole("heading", { level: 3 })).toContainText("1.1");
      await page.getByRole("link", { name: m("policies.manage.campaigns") }).click();
      await page.waitForURL(/\/campaigns$/);
      await page.getByRole("button", { name: m("policies.campaigns.launch") }).click();
      await expect(page.getByTestId("campaign-list").locator("li")).toHaveCount(1);
      await expectAccessible(page, testInfo, `policy-campaigns-${locale}`);
      await page.getByTestId("campaign-list").getByRole("link", { name: m("policies.campaigns.compliance") }).click();
      await page.waitForURL(/\/compliance\?campaign=/);
      await expect(page.getByTestId("compliance-totals")).toBeVisible();
      await expect(page.getByTestId("compliance-by-unit")).toBeVisible();
      await expect(page.getByTestId("compliance-people").locator("tbody tr")).toHaveCount(2);
      await expectAccessible(page, testInfo, `policy-compliance-${locale}`);
      await page.getByRole("button", { name: m("policies.compliance.remind") }).click();
      await expect(page.getByRole("main").getByRole("status")).toBeVisible();
      await page.getByTestId("compliance-people").getByRole("link").first().click();
      await page.waitForURL(/\/policies\/compliance\/people\/[0-9a-f-]{36}$/);
      await expect(page.getByTestId("person-policies").getByRole("link", { name: names[locale] })).toBeVisible();
      await expectAccessible(page, testInfo, `policy-person-${locale}`);
    });
  });
}
