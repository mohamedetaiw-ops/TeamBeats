import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 034 T025: the R2 evaluation pages. An administrator saves the production sampling policy, opens an agent's
 * production quality and drift alerts and its online comparisons, reads the review queue, sets a business outcome
 * baseline, sets a stricter rule and a dataset for an organization unit, and sees the opt-out in their own settings. In
 * English and Arabic with axe on every screen. The API decides every rule; these steps check what the pages send and show.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`R2 evaluation pages in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let agent: string;
    let unitId: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const agentName = locale === "ar" ? "المعرفة المؤسسية" : "Enterprise Knowledge";
    const unitName = locale === "ar" ? "خدمات الموظفين" : "People services";

    test.beforeAll(async () => {
      tenant = await createTenant(`evalp-${locale}`, locale, "enterprise_gov");
      const api = await tenantApi(tenant);
      const unit = await api.post("/api/v1/org/units", {
        data: { parentId: null, typeKey: "department", code: "PEOPLE", names: { en: "People services", ar: "خدمات الموظفين" } },
      });
      expect(unit.ok(), await unit.text()).toBe(true);
      unitId = ((await unit.json()) as { unit: { id: string } }).unit.id;
      const created = await api.post("/api/v1/agents", { data: { templateId: "enterprise_knowledge", orgUnitId: unitId } });
      expect(created.status(), await created.text()).toBe(201);
      agent = ((await created.json()) as { agent: { id: string } }).agent.id;
      await api.dispose();
    });

    test("production policy, agent quality, review, outcomes, units and the opt-out", async ({ page }, testInfo) => {
      test.setTimeout(240_000);
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);

      // The production policy: turning sampling on and saving it.
      await page.goto(`${origin}/admin/evaluation/production`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("evaluation.production.title"));
      const policy = page.getByTestId("eval-production-policy");
      await policy.getByRole("switch", { name: m("evaluation.production.enabled") }).check();
      await policy.getByLabel(m("evaluation.production.rate")).fill("5");
      await page.getByTestId("eval-production-save").click();
      await expect(page.getByRole("status").filter({ hasText: m("evaluation.production.saved") })).toBeVisible();
      await expectAccessible(page, testInfo, `evaluation-production-policy-${locale}`);

      // The agent's production quality: nothing sampled yet and no drift. The dashboard links to it.
      await page.goto(`${origin}/admin/evaluation/agents/${agent}`);
      await page.getByRole("link", { name: m("evaluation.dashboard.production") }).click();
      await page.waitForURL(/\/production$/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(agentName);
      await expect(page.getByText(m("evaluation.productionView.noSamples"))).toBeVisible();
      await expect(page.getByTestId("eval-drift-none")).toHaveText(m("evaluation.drift.none"));
      await expectAccessible(page, testInfo, `evaluation-agent-production-${locale}`);

      // Online comparisons: a draft agent has no version in force, so nothing can be proposed yet.
      await page.getByRole("link", { name: m("evaluation.dashboard.comparisons") }).click();
      await page.waitForURL(/\/comparisons$/);
      await expect(page.getByText(m("evaluation.comparisons.empty"))).toBeVisible();
      await expect(page.getByTestId("eval-comparison-propose")).toHaveCount(0);
      await expectAccessible(page, testInfo, `evaluation-comparisons-${locale}`);

      // The review queue, open and reviewed.
      await page.goto(`${origin}/admin/evaluation/review`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("evaluation.review.title"));
      await expect(page.getByTestId("eval-review-empty")).toHaveText(m("evaluation.review.empty.open"));
      await page.getByRole("link", { name: m("evaluation.review.statuses.reviewed") }).click();
      await expect(page.getByTestId("eval-review-empty")).toHaveText(m("evaluation.review.empty.reviewed"));
      await expectAccessible(page, testInfo, `evaluation-review-${locale}`);

      // Business outcomes: setting a baseline for the agent.
      await page.getByRole("navigation", { name: m("evaluation.tabsLabel") }).getByRole("link", { name: m("evaluation.tabs.outcomes") }).click();
      await page.waitForURL(/\/admin\/evaluation\/outcomes/);
      const outcomes = page.getByTestId("eval-outcomes");
      await expect(outcomes.getByRole("row", { name: new RegExp(agentName) })).toContainText(m("evaluation.outcomes.noBaseline"));
      await outcomes.getByRole("button", { name: m("evaluation.outcomes.edit", { agent: agentName }) }).click();
      await page.getByLabel(m("evaluation.outcomes.taskName", { language: m("evaluation.locales.en") })).fill("Answer a policy question");
      await page.getByLabel(m("evaluation.outcomes.taskName", { language: m("evaluation.locales.ar") })).fill("الإجابة عن سؤال في السياسات");
      await page.getByLabel(m("evaluation.outcomes.minutes")).fill("12");
      await page.getByTestId("eval-outcome-save").click();
      await expect(page.getByRole("status").filter({ hasText: m("evaluation.outcomes.saved") })).toBeVisible();
      await expect(outcomes.getByRole("row", { name: new RegExp(agentName) })).not.toContainText(m("evaluation.outcomes.noBaseline"));
      await expectAccessible(page, testInfo, `evaluation-outcomes-${locale}`);

      // Settings by unit: a stricter threshold and a unit dataset.
      await page.goto(`${origin}/admin/evaluation/units`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("evaluation.units.title"));
      await page.getByRole("navigation", { name: m("evaluation.units.choose") }).getByRole("link", { name: unitName }).click();
      await page.waitForURL(new RegExp(`unit=${unitId}`));
      await expect(page.getByTestId("eval-unit-chosen")).toHaveText(m("evaluation.units.editing", { unit: unitName }));
      await page.getByLabel(m("evaluation.suite.addMetric")).selectOption("groundedness");
      await page.getByLabel(m("evaluation.suite.thresholdFor", { metric: m("evaluation.metrics.groundedness") })).fill("99");
      await page.getByTestId("eval-unit-save").click();
      await expect(page.getByRole("status").filter({ hasText: m("evaluation.units.saved") })).toBeVisible();
      await expect(page.getByTestId("eval-unit-rules").getByRole("row", { name: new RegExp(m("evaluation.metrics.groundedness")) })).toContainText(
        m("evaluation.level.org"),
      );
      await page.getByLabel(m("evaluation.units.nameEn")).fill("Unit checks");
      await page.getByLabel(m("evaluation.units.nameAr")).fill("فحوص الوحدة");
      await page.getByTestId("eval-unit-dataset-create").click();
      await expect(page.getByRole("status").filter({ hasText: m("evaluation.units.created") })).toBeVisible();
      await expect(page.getByRole("checkbox", { name: locale === "ar" ? "فحوص الوحدة" : "Unit checks" })).toBeVisible();
      await expectAccessible(page, testInfo, `evaluation-units-${locale}`);

      // The person's own opt-out, offered because the package has production evaluation.
      await page.goto(`${origin}/settings`);
      const optOut = page.getByTestId("eval-opt-out").getByRole("switch", { name: m("preferences.evaluation.optOut") });
      await expect(optOut).not.toBeChecked();
      await optOut.check();
      await expect(optOut).toBeChecked();
      await page.reload();
      await expect(page.getByTestId("eval-opt-out").getByRole("switch", { name: m("preferences.evaluation.optOut") })).toBeChecked();
      await expectAccessible(page, testInfo, `evaluation-opt-out-${locale}`);
    });
  });
}
