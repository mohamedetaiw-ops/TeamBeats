import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 028 R3 T016 (US10): the administrator sees the session of a classroom lesson in the builder; Layla books a seat
 * from the course; Omar, the facilitator, finds the session among his own and sees Layla on the roster, with attendance
 * waiting for the start. Waiting lists, reminders, attendance and refusals: LearningSessionTests. In English and Arabic,
 * with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`classroom sessions in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const course = locale === "ar" ? "التدريب العملي لمسؤول الحريق" : "Fire warden practical";
    let tenant: TestTenant;
    let courseId: string;
    let sessionId: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`lse-${locale}`, locale, "workplace_plus", ["layla", "omar"]);
      const omar = await tenantApiAs(tenant, `omar@${tenant.key}.example`);
      const omarId = ((await (await omar.get("/api/v1/me")).json()) as { id: string }).id;
      await omar.dispose();
      const api = await tenantApi(tenant);
      const created = await api.post("/api/v1/learning/manage/courses", {
        data: { openEnrolment: true, definition: { titles: { [locale]: course }, lessons: [{ key: "practical", kind: "classroom", titles: { [locale]: locale === "ar" ? "عملي" : "Practical" } }] } },
      });
      if (!created.ok()) throw new Error(`Course failed with ${created.status()}: ${await created.text()}`);
      const { id, revision } = (await created.json()) as { id: string; revision: number };
      courseId = id;
      await api.post(`/api/v1/learning/manage/courses/${id}:publish`, { data: { revision, requiresRetraining: false } });
      const starts = Date.now() + 2 * 86_400_000;
      const session = await api.post(`/api/v1/learning/manage/courses/${id}/sessions`, {
        data: { lessonKey: "practical", startsAt: new Date(starts).toISOString(), endsAt: new Date(starts + 7_200_000).toISOString(), place: "Training room 2", capacity: 12, facilitatorId: omarId },
      });
      if (!session.ok()) throw new Error(`Session failed with ${session.status()}: ${await session.text()}`);
      sessionId = ((await session.json()) as { id: string }).id;
      await api.dispose();
      const layla = await tenantApiAs(tenant, `layla@${tenant.key}.example`);
      await layla.post(`/api/v1/learning/courses/${id}:enrol`);
      await layla.dispose();
    });

    test("the administrator sees the lesson's session in the builder", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/learning/manage/courses/${courseId}`);
      const sessions = page.getByRole("group", { name: m("learning.sessions.title") });
      await expect(sessions.getByRole("link", { name: m("learning.sessions.roster") })).toBeVisible();
      await expectAccessible(page, testInfo, `learning-sessions-builder-${locale}`);
    });

    test("Layla books a seat from the course", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/learning/courses/${courseId}`);
      const session = page.getByTestId("learning-session");
      await expect(session).toBeVisible();
      const book = session.getByRole("button", { name: m("learning.sessions.book") });
      // A click before the player has hydrated does nothing, so booking is tried again until the seat shows.
      await expect(async () => {
        if (await book.isVisible()) await book.click({ timeout: 2_000 });
        await expect(session.getByText(m("learning.sessions.seat.booked"))).toBeVisible({ timeout: 3_000 });
      }).toPass({ timeout: 30_000 });
      await expectAccessible(page, testInfo, `learning-sessions-player-${locale}`);
    });

    test("Omar finds his session and sees Layla on the roster", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `omar@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/learning`);
      await page.getByRole("link", { name: m("learning.sessions.facilitatingTitle") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("learning.sessions.facilitatingTitle"));
      await expectAccessible(page, testInfo, `learning-sessions-facilitating-${locale}`);
      await page.getByRole("link", { name: course }).click();
      await page.waitForURL(`**/learning/sessions/${sessionId}`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(course);
      await expect(page.getByText(m("learning.sessions.attendanceLater"))).toBeVisible();
      await expect(page.getByRole("checkbox", { name: /layla/i })).toBeDisabled();
      await expectAccessible(page, testInfo, `learning-sessions-roster-${locale}`);
    });
  });
}
