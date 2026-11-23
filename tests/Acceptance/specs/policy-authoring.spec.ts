import type { Page } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";
import { tenantApi, tenantApiAs } from "../support/pages";

/**
 * Spec 025 T011 (second part): a compliance officer creates a policy and writes two clauses in both languages; a second
 * compliance officer opens the approval from the inbox, comments on a clause and approves it; the first launches the
 * attestation campaign and follows it in the compliance view down to one person. In English and Arabic, with axe on
 * every screen. Spec 025 T013: the scheduled review of a policy in force opens in the owner's inbox, the owner records
 * "no change" from the review page with the keyboard, and the review history shows it.
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

    test("answer a scheduled review from the inbox", async ({ page }, testInfo) => {
      // The policy maintenance worker runs once a minute, so the review can take up to a minute to open.
      test.setTimeout(180_000);
      // A policy in force with a one-month cycle, so its 60-day review window is open at once.
      const admin = await tenantApi(tenant);
      const created = await admin.post("/api/v1/policies", { data: { code: "TRV-01", names: { en: "Travel", ar: "السفر" }, reviewMonths: 1, reviewWindowDays: 60 } });
      expect(created.ok(), await created.text()).toBe(true);
      const id = ((await created.json()) as { id: string }).id;
      const body = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });
      const draft = await admin.put(`/api/v1/policies/${id}/draft`, {
        data: { titles: { en: "Travel", ar: "السفر" }, clauses: [{ level: 1, titles: { en: "Booking", ar: "الحجز" }, bodies: { en: body("Book early."), ar: body("احجز مبكرًا.") } }] },
      });
      expect(draft.ok(), await draft.text()).toBe(true);
      expect((await admin.post(`/api/v1/policies/${id}/submit`, { data: {} })).ok()).toBe(true);
      const rana = await tenantApiAs(tenant, `rana@${tenant.key}.example`);
      const tasks = (await (await rana.get("/api/v1/tasks")).json()) as { source: string; taskId: string }[];
      const approval = tasks.find((t) => t.source === "policy_approval");
      expect(approval).toBeDefined();
      expect((await rana.post(`/api/v1/tasks/${approval!.taskId}/decision`, { data: { outcome: "approve" } })).ok()).toBe(true);
      await rana.dispose();
      // The maintenance worker opens the review within a minute.
      await expect
        .poll(async () => ((await (await admin.get(`/api/v1/policies/${id}/reviews`)).json()) as { open: unknown }).open !== null, { timeout: 90_000, intervals: [2_000] })
        .toBe(true);
      await admin.dispose();

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/tasks`);
      await page.getByTestId("task-inbox").getByRole("link", { name: locale === "ar" ? "السفر" : "Travel" }).click();
      await page.waitForURL(/\/reviews$/);
      await expect(page.getByTestId("review-open")).toBeVisible();
      await expectAccessible(page, testInfo, `policy-scheduled-review-${locale}`);
      const noChange = page.getByRole("radio", { name: m("policies.reviews.outcomes.no_change") });
      await noChange.focus();
      await page.keyboard.press("Space");
      await expect(noChange).toBeChecked();
      await page.getByLabel(m("policies.reviews.note")).fill(locale === "ar" ? "ما زالت صحيحة." : "Still right.");
      await page.getByRole("button", { name: m("policies.reviews.submit") }).click();
      await expect(page.getByTestId("review-done").getByRole("status")).toBeVisible();
      await expect(page.getByTestId("review-history").locator("tbody tr")).toHaveCount(1);
      await expect(page.getByTestId("review-history")).toContainText(m("policies.reviews.outcomes.no_change"));
      await expectAccessible(page, testInfo, `policy-scheduled-review-done-${locale}`);
    });
  });
}
