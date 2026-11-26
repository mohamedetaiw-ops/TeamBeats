import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 028 R3 T014 (US9): Layla added a skill at a basic level; My learning recommends the open course tagged with it,
 * and once she says it is not for her it goes. Ranking, skills off and other tenants: LearningRecommendationTests.
 * In English and Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`learning recommendations in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const course = locale === "ar" ? "مدخل إلى تحليل البيانات" : "Data analysis basics";
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`lrc-${locale}`, locale, "workplace_plus", ["layla"]);
      const api = await tenantApi(tenant);
      const current = await api.get("/api/v1/people/skills");
      const list = await api.put("/api/v1/people/skills", {
        headers: { "if-match": current.headers()["etag"] ?? "" },
        data: { skills: [{ names: { [locale]: locale === "ar" ? "تحليل البيانات" : "Data analysis" } }] },
      });
      if (!list.ok()) throw new Error(`Skill list failed with ${list.status()}: ${await list.text()}`);
      const skill = ((await list.json()) as { skills: { id: string }[] }).skills[0]!.id;
      const quiz = {
        questions: [{ kind: "true_false", prompts: { [locale]: "Fictional" }, options: [{ labels: { [locale]: "1" }, correct: true }, { labels: { [locale]: "2" }, correct: false }] }],
      };
      const created = await api.post("/api/v1/learning/manage/courses", {
        data: { openEnrolment: true, skills: [skill], definition: { titles: { [locale]: course }, lessons: [{ kind: "quiz", titles: { [locale]: "Quiz" }, quiz }] } },
      });
      if (!created.ok()) throw new Error(`Course failed with ${created.status()}: ${await created.text()}`);
      const { id, revision } = (await created.json()) as { id: string; revision: number };
      await api.post(`/api/v1/learning/manage/courses/${id}:publish`, { data: { revision, requiresRetraining: false } });
      await api.dispose();
      const layla = await tenantApiAs(tenant, `layla@${tenant.key}.example`);
      const mine = await layla.put("/api/v1/me/skills", { data: { skills: [{ skillId: skill, proficiency: "basic" }] } });
      if (!mine.ok()) throw new Error(`Own skills failed with ${mine.status()}: ${await mine.text()}`);
      await layla.dispose();
    });

    test("a course for a skill Layla is growing is recommended until she says no", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/learning`);
      const section = page.getByRole("region", { name: m("learning.recommended.title") });
      await expect(section.getByRole("link", { name: course })).toBeVisible();
      await expectAccessible(page, testInfo, `learning-recommended-${locale}`);
      const dismiss = section.getByRole("button", { name: m("learning.recommended.dismissCourse", { title: course }) });
      // A click before hydration does nothing, so it is tried again until the course goes.
      await expect(async () => {
        if (await dismiss.isVisible()) await dismiss.click({ timeout: 2_000 });
        await expect(section).toHaveCount(0, { timeout: 3_000 });
      }).toPass({ timeout: 30_000 });
      await page.reload();
      await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
      await expect(page.getByRole("region", { name: m("learning.recommended.title") })).toHaveCount(0);
    });
  });
}
