import { request, type APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { expectDetailConformant } from "../support/a11y";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

async function signedIn(tenant: TestTenant, email: string): Promise<APIRequestContext> {
  const host = `${tenant.key}.${stack.baseDomain}`;
  const anonymous = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host } });
  const response = await anonymous.post("/dev/signin", { data: { email } });
  const token = /tb_session=([^;]+)/.exec(response.headers()["set-cookie"] ?? "")?.[1];
  await anonymous.dispose();
  if (!response.ok() || !token) throw new Error(`Sign-in failed with ${response.status()}.`);
  return request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host, cookie: `tb_session=${token}` } });
}

const profileId = async (api: APIRequestContext) => ((await (await api.get("/api/v1/me/profile")).json()) as { profile: { id: string } }).profile.id;

/**
 * Spec 022 R3 T027 (US9 scenario 4): without an HR connection HR sets Rana's probation end date by hand. Rana has no
 * line manager, so HR reviews: the review sits in the task inbox, the milestone page lists it, and HR extends the
 * probation with a comment, which moves the date. In English and Arabic, with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`milestones in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let milestoneId = "";
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const day = (offset: number) => new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10);

    test.beforeAll(async () => {
      tenant = await createTenant(`ms-${locale}`, locale, "workplace_plus", ["rana"]);
      const admin = await signedIn(tenant, tenant.adminEmail);
      const rana = await signedIn(tenant, `rana@${tenant.key}.example`);
      const set = await admin.put(`/api/v1/hr/milestones/people/${await profileId(rana)}/probation`, { data: { dueDate: day(10) } });
      expect(set.ok(), await set.text()).toBe(true);
      milestoneId = ((await set.json()) as { id: string }).id;
      await Promise.all([admin.dispose(), rana.dispose()]);
    });

    test("HR sees the milestones of the people they cover", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/hr/milestones`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("milestones.title"));
      await expect(page.getByRole("cell", { name: m("milestones.kinds.probation") })).toBeVisible();
      await expect(page.getByRole("heading", { name: m("milestones.set.title") })).toBeVisible();
      await expectAccessible(page, testInfo, `milestones-${locale}`);
    });

    test("HR reviews a probation and extends it with a comment", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/hr/milestones/${milestoneId}`);
      await expect(page.getByRole("heading", { name: m("milestones.review.title") })).toBeVisible();
      await expectAccessible(page, testInfo, `milestone-review-${locale}`);
      await expectDetailConformant(page, testInfo, tenant, "/hr/milestones/[id]");

      await page.getByLabel(m("milestones.outcomes.extend")).check();
      await page.getByLabel(m("milestones.review.newDate")).fill(day(70));
      await page.getByLabel(m("milestones.review.comment")).fill("Three more months to settle into the role.");
      await page.getByRole("button", { name: m("milestones.review.submit") }).click();
      await expect(page.getByText(m("milestones.review.saved"))).toBeVisible();
      await expect(page.getByRole("heading", { name: m("milestones.review.title") })).toHaveCount(0);
      await expect(page.getByText("Three more months to settle into the role.")).toBeVisible();
      await expect(page.getByText(m("milestones.writeBack.none"))).toBeVisible();
      await expectAccessible(page, testInfo, `milestone-reviewed-${locale}`);
    });
  });
}
