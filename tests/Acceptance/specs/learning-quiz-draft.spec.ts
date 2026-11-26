import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 028 R3 T013 (US8): the administrator asks for quiz questions drafted from the course's page lesson; they arrive
 * marked as AI drafts, publishing is refused until each is kept, and keeping them lets the course publish. The model
 * is the fake gateway provider of the stack. Rules, refusals and other tenants: LearningQuizDraftTests. In English and
 * Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`quiz drafts in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    const m = (key: string, values: Record<string, string | number> = {}) => msg(locale, key, values as Record<string, string>);
    let tenant: TestTenant;
    let courseId: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`lq-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const me = (await (await api.get("/api/v1/me/profile")).json()) as { profile: { id: string } };
      const body = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });
      const created = await api.post("/api/v1/content/items", {
        data: { type: "page", ownerId: me.profile.id, variants: { [locale]: { title: "Fire exits", body: body("Every floor has two marked fire exits.") } } },
      });
      if (!created.ok()) throw new Error(`Page failed with ${created.status()}: ${await created.text()}`);
      const page = ((await created.json()) as { id: string }).id;
      await api.post(`/api/v1/content/items/${page}/publish`, { data: {} });
      const course = await api.post("/api/v1/learning/manage/courses", {
        data: { definition: { titles: { [locale]: "Fire safety" }, lessons: [{ kind: "page", titles: { [locale]: "Fire exits" }, sources: { [locale]: page } }] } },
      });
      if (!course.ok()) throw new Error(`Course failed with ${course.status()}: ${await course.text()}`);
      courseId = ((await course.json()) as { id: string }).id;
      await api.dispose();
    });

    test("drafted questions wait for a person before the course publishes", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/learning/manage/courses/${courseId}`);
      const panel = page.getByRole("group", { name: m("learning.builder.quizDraft.title") });
      await expect(panel).toBeVisible();
      await panel.getByLabel(m("learning.builder.quizDraft.count")).fill("2");
      const drafted = page.getByTestId("ai-draft-question");
      // A click before the builder has hydrated does nothing, so the request is tried again until questions show.
      await expect(async () => {
        if ((await drafted.count()) === 0) await panel.getByRole("button", { name: m("learning.builder.quizDraft.ask") }).click({ timeout: 2_000 });
        await expect(drafted).toHaveCount(2, { timeout: 5_000 });
      }).toPass({ timeout: 30_000 });
      await expectAccessible(page, testInfo, `learning-quiz-draft-${locale}`);

      await page.getByRole("button", { name: m("learning.builder.publish") }).click();
      await expect(page.getByText(m("learning.builder.failed")).first()).toBeVisible();
      await expect(page.getByText(m("learning.builder.version", { version: 1 }))).toHaveCount(0);

      for (let i = 0; i < 2; i++) await drafted.first().getByRole("button", { name: m("learning.builder.quizDraft.keep") }).click();
      await expect(drafted).toHaveCount(0);
      await page.getByRole("button", { name: m("learning.builder.publish") }).click();
      await expect(page.getByText(m("learning.builder.version", { version: 1 }))).toBeVisible();
    });
  });
}
