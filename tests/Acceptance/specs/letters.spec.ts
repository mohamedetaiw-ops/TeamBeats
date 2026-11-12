import { request, type APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
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

/**
 * Spec 022 T021: HR letters in the browser. HR sets up a letter type issued at once; Layla asks for it addressed to a
 * bank and downloads it; anyone holding the letter checks its code on the public page, which shows only the
 * reference, type, date and company. In English and Arabic, with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`letters in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    let code = "";
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`lt-${locale}`, locale, "workplace_plus", ["layla"]);
      laylaEmail = `layla@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const type = await admin.put("/api/v1/hr/letter-types/employment", {
        data: {
          names: { en: "Employment certificate", ar: "شهادة عمل" },
          templates: {
            en: "{addressee}\n\nThis is to certify that {name} works at {company}.\n\n{date}",
            ar: "{addressee}\n\nنشهد بأن {name} يعمل لدى {company}.\n\n{date}",
          },
          manualFields: [],
          needsApproval: false,
        },
      });
      expect(type.ok(), await type.text()).toBe(true);
      await admin.dispose();
    });

    test("an employee asks for a letter and downloads it", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/letters`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("letters.title"));
      await expect(page.getByText(m("letters.issuedAtOnce"))).toBeVisible();
      await expectAccessible(page, testInfo, `letters-${locale}`);

      await page.getByLabel(m("letters.addressee")).fill("Emirates Bank");
      await page.getByRole("button", { name: m("letters.send") }).click();
      await expect(page.getByText(m("letters.issuedNow", { reference: "LT-000001" }))).toBeVisible();
      const download = page.getByRole("link", { name: m("letters.download") });
      await expect(download).toBeVisible();
      await expectAccessible(page, testInfo, `letters-issued-${locale}`);

      await page.getByRole("link", { name: "LT-000001" }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("letters.detail.title"));
      await expect(page.getByText("Emirates Bank")).toBeVisible();
      await expectAccessible(page, testInfo, `letter-detail-${locale}`);

      const layla = await signedIn(tenant, laylaEmail);
      const letters = (await (await layla.get("/api/v1/me/letters")).json()) as { id: string }[];
      const pdf = await layla.get(`/api/v1/hr/letters/${letters[0]!.id}/pdf`);
      expect(pdf.headers()["content-type"]).toContain("application/pdf");
      code = /Code ([A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4})/.exec((await pdf.body()).toString("latin1"))?.[1] ?? "";
      expect(code).not.toBe("");
      await layla.dispose();
    });

    test("anyone checks the code on the public page", async ({ page }, testInfo) => {
      await page.goto(`${hostOrigin(tenant.key)}/letters/verify?code=${encodeURIComponent(code.toLowerCase())}`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("letters.verify.title"));
      await expect(page.getByText(m("letters.verify.valid"))).toBeVisible();
      await expect(page.getByText("LT-000001")).toBeVisible();
      await expect(page.getByText(locale === "ar" ? "شهادة عمل" : "Employment certificate")).toBeVisible();
      await expect(page.getByText("layla", { exact: false })).toHaveCount(0);
      await expectAccessible(page, testInfo, `letters-verify-${locale}`);

      await page.getByLabel(m("letters.verify.code")).fill("AAAA-AAAA-AAAA");
      await page.getByRole("button", { name: m("letters.verify.check") }).click();
      await expect(page.getByText(m("letters.verify.notFoundTitle"))).toBeVisible();
      await expectAccessible(page, testInfo, `letters-verify-missing-${locale}`);
    });

    test("HR sees the letter type and the letter", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/letters`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("letters.admin.title"));
      await expect(page.getByLabel(m("letters.admin.settings.verification"))).toBeChecked();
      await expect(page.getByRole("link", { name: "LT-000001" })).toBeVisible();
      await expectAccessible(page, testInfo, `letters-admin-${locale}`);
    });
  });
}
