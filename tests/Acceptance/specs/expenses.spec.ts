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
 * Spec 022 T023: expense claims in the browser. Finance sets a policy in AED and a dollar rate; Layla sends a claim
 * with a dollar meal over the limit with a justification and a per diem, and sees both amounts. HR approves both
 * steps (she has no line manager, so finance takes the first step), and the claim shows it is paid outside
 * TeamBeats. In English and Arabic, with axe on every screen. Values are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`expenses in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    let claimId = "";
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const today = new Date().toISOString().slice(0, 10);

    test.beforeAll(async () => {
      tenant = await createTenant(`ex-${locale}`, locale, "workplace_plus", ["layla"]);
      laylaEmail = `layla@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const current = await admin.get("/api/v1/hr/expense-policy");
      const policy = await admin.put("/api/v1/hr/expense-policy", {
        headers: { "if-match": current.headers()["etag"] ?? "" },
        data: {
          currency: "AED",
          windowDays: 60,
          categories: [
            { key: "meals", names: { en: "Meals", ar: "الوجبات" }, kind: "amount", receipt: false, limits: [{ grade: null, amount: 200 }] },
            { key: "per_diem", names: { en: "Per diem", ar: "بدل يومي" }, kind: "per_diem", receipt: false, rate: 150 },
          ],
          tiers: [],
          costCentres: [],
        },
      });
      expect(policy.ok(), await policy.text()).toBe(true);
      const rates = await admin.put("/api/v1/hr/expense-rates", { data: { rates: [{ currency: "USD", date: "2026-01-01", rate: 3.6725 }] } });
      expect(rates.ok(), await rates.text()).toBe(true);
      await admin.dispose();
    });

    test("an employee sends a claim with a foreign amount and a per diem", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/expenses`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("expenses.pageTitle"));
      await expectAccessible(page, testInfo, `expenses-${locale}`);

      await page.getByLabel(m("expenses.title"), { exact: true }).fill("Client visit");
      const first = page.getByRole("group", { name: m("expenses.line", { n: "1" }) });
      await first.getByLabel(m("expenses.date")).fill(today);
      await first.getByLabel(m("expenses.amount")).fill("100");
      await first.getByLabel(m("expenses.currency")).fill("USD");
      await first.getByLabel(m("expenses.justification")).fill("Dinner with a client");
      await page.getByRole("button", { name: m("expenses.addLine") }).click();
      const second = page.getByRole("group", { name: m("expenses.line", { n: "2" }) });
      await second.getByLabel(m("expenses.category")).selectOption("per_diem");
      await second.getByLabel(m("expenses.days")).fill("2");
      await page.getByRole("button", { name: m("expenses.send") }).click();
      await expect(page.getByText(m("expenses.sent", { reference: "EX-000001" }))).toBeVisible();
      await expectAccessible(page, testInfo, `expenses-sent-${locale}`);

      await page.getByRole("link", { name: "EX-000001" }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("expenses.detail.title"));
      await expect(page.getByText(m("expenses.statuses.pending"))).toBeVisible();
      await expect(page.getByRole("button", { name: m("expenses.detail.withdraw") })).toBeVisible();
      await expectAccessible(page, testInfo, `expense-detail-${locale}`);
      claimId = page.url().split("/").pop()!;
    });

    test("finance approves it and sees it among the claims", async ({ page }, testInfo) => {
      const admin = await signedIn(tenant, tenant.adminEmail);
      for (let step = 0; step < 2; step++) {
        const tasks = (await (await admin.get("/api/v1/tasks")).json()) as { taskId: string; source: string; link: string }[];
        const task = tasks.find((i) => i.source === "expense" && i.link.includes(claimId));
        expect(task, "finance has the claim in the inbox").toBeTruthy();
        const decided = await admin.post(`/api/v1/tasks/${task!.taskId}/decision`, { data: { outcome: "approve", comment: null } });
        expect(decided.ok(), await decided.text()).toBe(true);
      }
      await admin.dispose();

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/expenses/${claimId}`);
      await expect(page.getByText(m("expenses.detail.outside"))).toBeVisible();
      await expectAccessible(page, testInfo, `expense-approved-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/admin/expenses`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("expenses.admin.title"));
      await expect(page.getByRole("link", { name: "EX-000001" })).toBeVisible();
      await expectAccessible(page, testInfo, `expenses-admin-${locale}`);
    });

    test("a receipt read with AI fills the line only after the employee confirms", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/expenses`);
      const first = page.getByRole("group", { name: m("expenses.line", { n: "1" }) });
      await first.getByLabel(m("expenses.receipt")).setInputFiles({ name: "receipt.pdf", mimeType: "application/pdf", buffer: Buffer.from("%PDF-1.4 fictional receipt") });
      await expect(first.getByText(m("expenses.receiptChosen", { name: "receipt.pdf" }))).toBeVisible();

      // Receipts are Confidential, and the default AI policy keeps them away from shared models.
      await first.getByRole("button", { name: m("expenses.readReceipt") }).click();
      await expect(first.getByText(m("expenses.readBlocked"))).toBeVisible();

      // With a policy that allows it, the values are shown apart from the line until the employee uses them.
      await page.route("**/api/v1/me/expenses/receipts/*:read", (route) =>
        route.fulfill({ json: { outcome: "suggested", suggestion: { merchant: "Fictional Cafe", date: today, amount: 42.5, currency: "SAR", tax: 5.5 } } }),
      );
      await first.getByLabel(m("expenses.receipt")).setInputFiles({ name: "receipt2.pdf", mimeType: "application/pdf", buffer: Buffer.from("%PDF-1.4 another fictional receipt") });
      await first.getByRole("button", { name: m("expenses.readReceipt") }).click();
      await expect(first.getByText(m("expenses.suggestedTitle"))).toBeVisible();
      await expect(first.getByText("Fictional Cafe")).toBeVisible();
      await expect(first.getByLabel(m("expenses.amount"))).toHaveValue("");
      await expectAccessible(page, testInfo, `expense-receipt-reading-${locale}`);
      await first.getByRole("button", { name: m("expenses.useValues") }).click();
      await expect(first.getByLabel(m("expenses.amount"))).toHaveValue("42.5");
      await expect(first.getByLabel(m("expenses.currency"))).toHaveValue("SAR");
      await expect(first.getByLabel(m("expenses.merchant"))).toHaveValue("Fictional Cafe");
      await expect(first.getByText(m("expenses.suggestedTitle"))).toBeHidden();
    });
  });
}
