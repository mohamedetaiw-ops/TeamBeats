import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 034 T016: the evaluation pages. An administrator opens the evaluation section, finds an agent made from the
 * Enterprise Knowledge template, creates a dataset, adds a case and imports more from CSV (one row is refused and
 * named), adds the dataset to the agent's suite, runs the evaluation twice, opens a case with its evidence, compares
 * the two runs, and reads the calibration and settings pages, saving a stricter rule. In English and Arabic with axe on
 * every screen. The API decides every rule; these steps check what the pages send and show.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`evaluation pages in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let agent: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const agentName = locale === "ar" ? "المعرفة المؤسسية" : "Enterprise Knowledge";
    const datasetName = locale === "ar" ? "أسئلة الإجازات" : "Leave questions";

    test.beforeAll(async () => {
      tenant = await createTenant(`eval-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const unit = await api.post("/api/v1/org/units", {
        data: { parentId: null, typeKey: "department", code: "PEOPLE", names: { en: "People services", ar: "خدمات الموظفين" } },
      });
      expect(unit.ok(), await unit.text()).toBe(true);
      const unitId = ((await unit.json()) as { unit: { id: string } }).unit.id;
      const created = await api.post("/api/v1/agents", { data: { templateId: "enterprise_knowledge", orgUnitId: unitId } });
      expect(created.status(), await created.text()).toBe(201);
      agent = ((await created.json()) as { agent: { id: string } }).agent.id;
      await api.dispose();
    });

    async function waitForCompleted(page: import("@playwright/test").Page) {
      await expect(page.getByTestId("eval-run-status")).toHaveText(m("evaluation.runStatus.completed"), { timeout: 120_000 });
    }

    test("build a suite, run it, open a case and compare runs", async ({ page }, testInfo) => {
      test.setTimeout(300_000);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin`);
      await page.getByRole("link", { name: m("admin.nav.evaluation"), exact: true }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("evaluation.title"));
      const overview = page.getByTestId("eval-agents");
      await expect(overview.getByRole("link", { name: agentName })).toBeVisible();
      await expectAccessible(page, testInfo, `evaluation-overview-${locale}`);

      // The dashboard: a draft with no version in force and no runs yet.
      await overview.getByRole("link", { name: agentName }).click();
      await page.waitForURL(/\/admin\/evaluation\/agents\/[0-9a-f-]{36}$/);
      await expect(page.getByTestId("eval-summary")).toHaveText(m("evaluation.dashboard.summaryNone"));
      await expect(page.getByText(m("evaluation.dashboard.noRuns"))).toBeVisible();
      await expectAccessible(page, testInfo, `evaluation-dashboard-empty-${locale}`);

      // The suites page shows the inherited suites and the rules with their levels; a new dataset opens its editor.
      await page.getByRole("link", { name: m("evaluation.dashboard.suite") }).click();
      await page.waitForURL(/\/suite$/);
      await expect(page.getByTestId("eval-inherited").getByRole("row")).not.toHaveCount(1);
      await expect(page.getByTestId("eval-rules")).toBeVisible();
      await expectAccessible(page, testInfo, `evaluation-suite-${locale}`);
      await page.getByLabel(m("evaluation.dataset.nameIn", { locale: m(`evaluation.locales.${locale}`) }), { exact: true }).fill(datasetName);
      await page.getByTestId("eval-dataset-create").click();
      await page.waitForURL(/\/admin\/evaluation\/datasets\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(datasetName);
      await expect(page.getByText(m("evaluation.dataset.noCases"))).toBeVisible();
      await expectAccessible(page, testInfo, `evaluation-dataset-empty-${locale}`);

      // A case entered by hand makes version 1.
      await page.getByLabel(m("evaluation.case.input"), { exact: true }).fill("How many days of annual leave do I get?");
      await page.getByLabel(m("evaluation.case.expect.keyFacts"), { exact: true }).fill("25 days");
      await page.getByTestId("eval-case-save").click();
      await expect(page.getByRole("status").filter({ hasText: m("evaluation.dataset.saved", { version: "1" }) })).toBeVisible();
      await expect(page.getByTestId("eval-dataset-cases").getByRole("row")).toHaveCount(2);

      // An import lists the refused row with its reason and adds nothing until it is confirmed.
      const csv = ["input,locale,key_facts,tags", "Who approves my leave?,en,your manager,approval", "Bad row,fr,anything,approval"].join("\n");
      await page.getByLabel(m("evaluation.dataset.content"), { exact: true }).fill(csv);
      await page.getByTestId("eval-import-check").click();
      const preview = page.getByTestId("eval-import-preview");
      await expect(preview.getByText(m("evaluation.dataset.previewSummary", { valid: "1", refused: "1" }))).toBeVisible();
      await expect(preview.getByRole("row")).toHaveCount(2);
      await expect(page.getByTestId("eval-dataset-cases").getByRole("row")).toHaveCount(2);
      await expectAccessible(page, testInfo, `evaluation-dataset-import-${locale}`);
      await page.getByTestId("eval-import-confirm").click();
      await expect(page.getByRole("status").filter({ hasText: m("evaluation.dataset.imported", { version: "2" }) })).toBeVisible();
      await expect(page.getByTestId("eval-dataset-cases").getByRole("row")).toHaveCount(3);

      // The dataset joins the agent's suite.
      await page.getByRole("link", { name: m("evaluation.dataset.back") }).click();
      await page.waitForURL(/\/suite$/);
      await page.getByRole("checkbox", { name: datasetName }).check();
      await page.getByTestId("eval-suite-save").click();
      await expect(page.getByRole("status").filter({ hasText: m("evaluation.suite.saved", { version: "1" }) })).toBeVisible();

      // Two runs of the draft, the second compared with the first.
      await page.getByRole("link", { name: m("evaluation.run.back") }).click();
      await page.waitForURL(/\/admin\/evaluation\/agents\/[0-9a-f-]{36}$/);
      await page.getByTestId("eval-start").click();
      await page.waitForURL(/\/admin\/evaluation\/runs\/[0-9a-f-]{36}$/);
      await waitForCompleted(page);
      await expect(page.getByTestId("eval-run-cases").getByRole("row")).not.toHaveCount(1);
      await expectAccessible(page, testInfo, `evaluation-run-${locale}`);

      await page.getByTestId("eval-run-cases").getByRole("link", { name: m("evaluation.run.caseNumber", { n: "1" }), exact: true }).click();
      await page.waitForURL(/\/cases\/1$/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("evaluation.case.title", { n: "1" }));
      await expect(page.getByTestId("eval-case-result")).not.toHaveText(m("evaluation.none"));
      await expectAccessible(page, testInfo, `evaluation-case-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/admin/evaluation/agents/${agent}`);
      await page.getByTestId("eval-start").click();
      await page.waitForURL(/\/admin\/evaluation\/runs\/[0-9a-f-]{36}$/);
      await waitForCompleted(page);
      await page.getByRole("link", { name: m("evaluation.run.back") }).click();
      await expect(page.getByTestId("eval-runs").getByRole("row")).toHaveCount(3);
      await expect(page.getByTestId("eval-trend")).toBeVisible();
      await expectAccessible(page, testInfo, `evaluation-dashboard-${locale}`);
      await page.getByRole("button", { name: m("evaluation.dashboard.compareButton") }).click();
      await page.waitForURL(/\/admin\/evaluation\/compare\?a=/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("evaluation.compare.title", { a: "1", b: "1" }));
      await expect(page.getByTestId("eval-compare-metrics")).toBeVisible();
      await expect(page.getByTestId("eval-compare-sides")).toBeVisible();
      await expectAccessible(page, testInfo, `evaluation-compare-${locale}`);

      // Calibration: the rubrics with their state; the administrator may calibrate, labelling is for reviewers.
      await page.getByRole("link", { name: m("evaluation.tabs.calibration"), exact: true }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("evaluation.calibration.title"));
      await expect(page.getByTestId("eval-rubric-state-relevance")).toHaveText(m("evaluation.calibration.states.platform"));
      await page.getByRole("link", { name: m("evaluation.metrics.relevance") }).click();
      await expect(page.getByRole("heading", { name: m("evaluation.calibration.set", { rubric: m("evaluation.metrics.relevance") }) })).toBeVisible();
      await expectAccessible(page, testInfo, `evaluation-calibration-${locale}`);

      // Settings: a stricter tenant rule is saved.
      await page.getByRole("link", { name: m("evaluation.tabs.settings"), exact: true }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("evaluation.settings.title"));
      await page.getByLabel(m("evaluation.suite.addMetric"), { exact: true }).selectOption({ label: m("evaluation.metrics.groundedness") });
      await page.getByLabel(m("evaluation.suite.thresholdFor", { metric: m("evaluation.metrics.groundedness") }), { exact: true }).fill("95");
      await page.getByTestId("eval-settings-save").click();
      await expect(page.getByRole("status").filter({ hasText: m("evaluation.settings.saved") })).toBeVisible();
      await expectAccessible(page, testInfo, `evaluation-settings-${locale}`);
    });
  });
}
