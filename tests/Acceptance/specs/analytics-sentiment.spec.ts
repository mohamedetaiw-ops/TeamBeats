import { type APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

const people = ["p1", "p2", "p3", "p4", "p5", "p6"];
const answers = ["[fake:positive] A great week.", "[fake:positive] Good support.", "[fake:positive] Calm and productive.", "[fake:negative] Too many meetings.", "Nothing to add.", "It was a week."];

/**
 * Spec 021 T017 in the browser: six people answer an anonymous pulse survey that then closes; the administrator opens
 * sentiment from the analytics workspace, turns it on, and within a few seconds sees the labelled totals and the survey
 * as a topic, with no text shown. In English and Arabic, with axe. Names and answers are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`sentiment analytics in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`snt-${locale}`, locale, "workplace_plus", people);
      const admin = await tenantApi(tenant);
      const unit = await admin.post("/api/v1/org/units", {
        data: { parentId: null, typeKey: "department", code: "OPS", names: { en: "Operations", ar: "العمليات" } },
      });
      expect(unit.ok(), await unit.text()).toBe(true);
      const unitId = ((await unit.json()) as { unit: { id: string } }).unit.id;
      const clients: APIRequestContext[] = [];
      for (const person of people) {
        const client = await tenantApiAs(tenant, `${person}@${tenant.key}.example`);
        const me = (await (await client.get("/api/v1/me")).json()) as { id: string };
        const member = await admin.post(`/api/v1/org/units/${unitId}/memberships`, { data: { userId: me.id, type: "primary" } });
        expect(member.ok(), await member.text()).toBe(true);
        clients.push(client);
      }
      const draft = await admin.post("/api/v1/engagement/surveys", {
        data: {
          orgUnitId: unitId,
          title: "Pulse check",
          intro: "A short pulse check.",
          anonymous: true,
          threshold: null,
          closesAt: new Date(Date.now() + 7 * 86_400_000).toISOString(),
          questions: [{ kind: "text", prompt: "How was your week?", required: true, options: [] }],
        },
      });
      expect(draft.status(), await draft.text()).toBe(201);
      const surveyId = ((await draft.json()) as { id: string }).id;
      const published = await admin.post(`/api/v1/engagement/surveys/${surveyId}:publish`);
      expect(published.ok(), await published.text()).toBe(true);
      const questionId = ((await published.json()) as { questions: { id: string }[] }).questions[0].id;
      for (const [i, client] of clients.entries()) {
        const answered = await client.post(`/api/v1/engagement/surveys/${surveyId}/responses`, { data: { answers: [{ questionId, text: answers[i] }] } });
        expect(answered.ok(), await answered.text()).toBe(true);
        await client.dispose();
      }
      expect((await admin.post(`/api/v1/engagement/surveys/${surveyId}:close`)).ok()).toBe(true);
      await admin.dispose();
    });

    test("an administrator turns sentiment on and reads the labelled survey answers", async ({ page }, testInfo) => {
      test.setTimeout(180_000);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/analytics`);
      await page.getByRole("link", { name: m("analytics.workspace.sentimentLink") }).click();
      await page.waitForURL(/\/analytics\/sentiment$/);
      await expect(page.getByRole("heading", { level: 1, name: m("analytics.sentiment.title") })).toBeVisible();
      await expect(page.getByText(m("analytics.sentiment.off"), { exact: true })).toBeVisible();

      const toggle = page.getByRole("switch", { name: m("analytics.sentiment.switch") });
      await expect(toggle).not.toBeChecked();
      await toggle.check();
      await expect(toggle).toBeChecked();
      await expect(page.getByText(m("analytics.sentiment.empty"), { exact: true })).toBeVisible();

      // The background run labels closed survey answers within a few seconds.
      await expect(async () => {
        await page.reload();
        await expect(page.getByTestId("sentiment-total")).toBeVisible({ timeout: 2_000 });
      }).toPass({ timeout: 120_000, intervals: [5_000] });
      const total = page.getByTestId("sentiment-total");
      await expect(total).toContainText(m("analytics.sentiment.positive"));
      await expect(page.getByTestId("sentiment-topics")).toContainText("Pulse check");
      await expect(page.getByTestId("sentiment-sources")).toContainText(m("analytics.sentiment.source.survey_answer"));
      // Free text is never shown.
      await expect(page.getByText("Too many meetings")).toHaveCount(0);
      await expectAccessible(page, testInfo, `analytics-sentiment-${locale}`);
    });
  });
}
