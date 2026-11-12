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

/** A one pixel PNG and a tiny PDF. */
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==", "base64");
const pdf = Buffer.from("%PDF-1.4\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF");

/**
 * Spec 023 FR-003 (images and files in posts): Layla adds an image and a PDF in the composer and posts; the post shows
 * the image with a description and the PDF as a download. The checks behind uploads are covered by PostAttachmentTests.
 * In English and Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`post attachments in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    let community: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`pa-${locale}`, locale, "workplace", ["layla"]);
      laylaEmail = `layla@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const created = await admin.post("/api/v1/engagement/communities", { data: { names: { en: "Finance team", ar: "فريق المالية" }, kind: "open" } });
      expect(created.status(), await created.text()).toBe(201);
      community = ((await created.json()) as { id: string }).id;
      const layla = await signedIn(tenant, laylaEmail);
      const joined = await layla.post(`/api/v1/engagement/communities/${community}:join`);
      expect(joined.ok(), await joined.text()).toBe(true);
      await Promise.all([layla.dispose(), admin.dispose()]);
    });

    test("a member posts an image and a file and both show on the post", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities/${community}`);
      const body = page.getByRole("textbox", { name: m("engagement.composer.body") });
      await body.click();
      await body.pressSequentially(locale === "ar" ? "قائمة إغلاق الشهر" : "The month-end checklist");
      await page.getByLabel(m("engagement.attachments.add")).setInputFiles([
        { name: "closing.png", mimeType: "image/png", buffer: png },
        { name: "checklist.pdf", mimeType: "application/pdf", buffer: pdf },
      ]);
      const chosen = page.getByRole("list", { name: m("engagement.attachments.chosen") });
      await expect(chosen.getByRole("listitem")).toHaveCount(2);
      await expectAccessible(page, testInfo, `post-attachments-compose-${locale}`);
      await page.getByRole("button", { name: m("engagement.composer.submit"), exact: true }).click();
      await expect(page.getByText(m("engagement.composer.posted"))).toBeVisible();

      await expect(page.getByRole("img", { name: m("engagement.attachments.imageAlt", { name: "closing.png" }) }).first()).toBeVisible();
      await expect(page.getByRole("link", { name: /checklist\.pdf/ }).first()).toBeVisible();
      await expectAccessible(page, testInfo, `post-attachments-post-${locale}`);
    });
  });
}
