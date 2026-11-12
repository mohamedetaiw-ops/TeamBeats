import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 028 T004 and T005: the administrator builds a course with a quiz, opens it for enrolment and publishes it; Layla
 * finds it in the catalogue, starts it, passes the quiz and opens the certificate, then checks it by its ID. Scope,
 * audiences, attempts, sources and retraining are covered by LearningCourseTests; the administrator then makes the
 * course required for everyone with a rule (rules in detail: LearningAssignmentTests). In English and Arabic, with axe.
 * Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`learning in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string | number> = {}) => msg(locale, key, values as Record<string, string>);
    const language = new Intl.DisplayNames([locale], { type: "language" }).of(locale)!;
    const course = locale === "ar" ? "السلامة من الحرائق" : "Fire safety";
    const question = locale === "ar" ? "أين مخرج الطوارئ؟" : "Where is the emergency exit?";
    const right = locale === "ar" ? "عند نهاية الممر" : "At the end of the corridor";
    const wrong = locale === "ar" ? "في المصعد" : "In the lift";

    test.beforeAll(async () => {
      tenant = await createTenant(`lr-${locale}`, locale, "workplace_plus", ["layla"]);
    });

    test("an administrator builds and publishes a course with a quiz", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/learning`);
      await page.getByRole("link", { name: m("learning.manage.pageTitle") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("learning.manage.pageTitle"));

      const create = page.getByRole("region", { name: m("learning.manage.newCourse") });
      await create.getByLabel(m("learning.manage.courseTitle")).fill(course);
      await expectAccessible(page, testInfo, `learning-manage-${locale}`);
      await create.getByRole("button", { name: m("learning.manage.createCourse") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(course);

      await page.getByLabel(m("learning.builder.openEnrolment")).check();
      await page.getByLabel(m("learning.builder.lessonKind")).selectOption("quiz");
      await page.getByRole("button", { name: m("learning.builder.addLesson") }).click();
      await page.getByLabel(m("learning.builder.lessonTitleIn", { language })).fill(locale === "ar" ? "اختبار قصير" : "Short quiz");
      await page.getByLabel(m("learning.builder.promptIn", { language })).fill(question);
      await page.getByLabel(m("learning.builder.optionIn", { number: 1, language })).fill(right);
      await page.getByLabel(m("learning.builder.optionIn", { number: 2, language })).fill(wrong);
      await expectAccessible(page, testInfo, `learning-builder-${locale}`);
      await page.getByRole("button", { name: m("learning.builder.publish") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("learning.builder.published") })).toBeVisible();
      await expect(page.getByText(m("learning.builder.version", { version: 1 }))).toBeVisible();
    });

    test("a colleague takes the course, passes the quiz and checks the certificate", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/learning/catalogue`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("learning.catalogue.pageTitle"));
      const main = page.getByRole("main");
      await main.getByLabel(m("learning.catalogue.search")).fill(course);
      await main.getByRole("button", { name: m("learning.catalogue.searchButton"), exact: true }).click();
      await expectAccessible(page, testInfo, `learning-catalogue-${locale}`);
      await main.getByRole("link", { name: course }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(course);

      await page.getByRole("button", { name: m("learning.player.enrol") }).click();
      const quiz = page.getByRole("group", { name: m("learning.player.quiz.question", { number: 1, prompt: question }) });
      await quiz.getByLabel(right).check();
      await expectAccessible(page, testInfo, `learning-player-${locale}`);
      await page.getByRole("button", { name: m("learning.player.quiz.submit") }).click();
      await expect(page.getByText(m("learning.player.completedTitle"))).toBeVisible();

      await page.getByRole("link", { name: m("learning.player.certificate") }).click();
      await expect(page.getByRole("heading", { level: 2, name: m("learning.certificate.heading") })).toBeVisible();
      const code = (await page.locator(".learning-code").textContent())!.trim();
      await expectAccessible(page, testInfo, `learning-certificate-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/learning/certificates/verify`);
      await main.getByLabel(m("learning.verify.code")).fill(code.toLowerCase().replaceAll("-", " "));
      await main.getByRole("button", { name: m("learning.verify.check"), exact: true }).click();
      await expect(page.getByText(m("learning.verify.validTitle"))).toBeVisible();

      await page.goto(`${hostOrigin(tenant.key)}/learning`);
      const completed = page.getByRole("region", { name: m("learning.mine.completed") });
      await expect(completed.getByRole("link", { name: course })).toBeVisible();
    });

    test("an administrator makes the course required for everyone with a rule", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/learning/manage`);
      await page.getByRole("main").getByRole("link", { name: course }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(course);
      await page.getByRole("tab", { name: m("learning.builder.rulesTab") }).click();
      await expect(page.getByText(m("learning.rules.emptyTitle"))).toBeVisible();
      await page.getByLabel(m("learning.rules.dueDays")).fill("14");
      await page.getByLabel(m("learning.rules.recurrence")).fill("12");
      await expectAccessible(page, testInfo, `learning-rules-${locale}`);
      await page.getByRole("button", { name: m("learning.rules.create") }).click();
      const rules = page.getByRole("list", { name: m("learning.rules.title") });
      await expect(rules.getByText(m("learning.rules.status.active"))).toBeVisible();
      await expect(rules.getByText(m("learning.rules.everyoneInScope"))).toBeVisible();
    });
  });
}
