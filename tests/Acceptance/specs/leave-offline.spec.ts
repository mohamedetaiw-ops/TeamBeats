import type { APIRequestContext } from "@playwright/test";
import {
  expect,
  expectAccessible,
  signInWithDevForm,
  test,
} from "../support/fixtures";
import { msg } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 030 R3 MOB-025 (T018): with no connection a leave request is kept as a draft on the device, encrypted with the
 * offline cache; back online it comes back filled in and is sent only when the person presses submit.
 */
test.describe("leave draft with no connection", () => {
  let tenant: TestTenant;
  const monday = (() => {
    const d = new Date();
    d.setUTCDate(d.getUTCDate() + 14);
    while (d.getUTCDay() !== 1) d.setUTCDate(d.getUTCDate() + 1);
    return d;
  })();
  const day = (offset: number) =>
    new Date(monday.getTime() + offset * 86_400_000).toISOString().slice(0, 10);
  const profileId = async (api: APIRequestContext) =>
    (
      (await (await api.get("/api/v1/me/profile")).json()) as {
        profile: { id: string };
      }
    ).profile.id;

  test.beforeAll(async () => {
    tenant = await createTenant("lvoff", "en", "workplace_plus", ["rana"]);
    const admin = await tenantApi(tenant);
    const rana = await tenantApiAs(tenant, `rana@${tenant.key}.example`);
    const me = await profileId(admin);
    expect(
      (
        await admin.put(`/api/v1/people/${me}/manager`, {
          data: { managerId: await profileId(rana) },
        })
      ).ok(),
    ).toBe(true);
    expect(
      (
        await admin.put("/api/v1/hr/leave/types/annual", {
          data: {
            names: { en: "Annual leave", ar: "إجازة سنوية" },
            sensitive: false,
          },
        })
      ).ok(),
    ).toBe(true);
    const policy = await admin.post("/api/v1/hr/leave/policies", {
      data: {
        typeKey: "annual",
        orgUnitId: null,
        bands: [{ days: 21 }],
        accrual: "yearly",
      },
    });
    expect(policy.status(), await policy.text()).toBe(201);
    const adjust = await admin.post("/api/v1/hr/leave/adjustments", {
      data: {
        userId: me,
        typeKey: "annual",
        leaveYear: monday.getUTCFullYear(),
        delta: 15,
        note: "Opening balance",
      },
    });
    expect(adjust.status(), await adjust.text()).toBe(201);
    await Promise.all([admin.dispose(), rana.dispose()]);
  });

  test("the request is kept on the device offline and sent once confirmed online", async ({
    page,
    context,
  }, testInfo) => {
    const f = (key: string) => msg("en", `leave.form.${key}`);
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.goto(`${hostOrigin(tenant.key)}/leave`);
    // The offline cache, whose key encrypts the draft, is in place.
    await expect
      .poll(() =>
        page.evaluate(
          () =>
            new Promise<boolean>((resolve) => {
              const request = indexedDB.open("tb-offline", 1);
              request.onupgradeneeded = () => request.transaction?.abort();
              request.onerror = () => resolve(false);
              request.onsuccess = () => {
                const db = request.result;
                if (!db.objectStoreNames.contains("cache"))
                  return resolve(false);
                const get = db
                  .transaction("cache")
                  .objectStore("cache")
                  .get("current");
                get.onsuccess = () => {
                  db.close();
                  resolve(get.result !== undefined);
                };
              };
            }),
        ),
      )
      .toBe(true);

    const form = page.locator("form.leave-form");
    // The page was opened online, so the form and its leave types are there before the connection goes.
    await expect(form.getByLabel(f("start"))).toBeVisible();
    await context.setOffline(true);
    await expect(form.getByText(f("offline"), { exact: true })).toBeVisible();
    await form.getByLabel(f("start")).fill(day(0));
    await form.getByLabel(f("end")).fill(day(2));
    await form.getByLabel(f("comment")).fill("Family visit");
    await form
      .getByRole("button", { name: f("saveDraft"), exact: true })
      .click();
    await expect(
      form.getByText(f("draftSaved"), { exact: true }),
    ).toBeVisible();
    await expectAccessible(page, testInfo, "leave-draft-offline");
    // The draft is ciphertext on the device.
    const plain = await page.evaluate(
      () =>
        new Promise<boolean>((resolve) => {
          const request = indexedDB.open("tb-offline", 1);
          request.onsuccess = () => {
            const get = request.result
              .transaction("cache")
              .objectStore("cache")
              .get("current");
            get.onsuccess = () => {
              const drafts =
                (
                  get.result as {
                    drafts?: Record<string, { data: ArrayBuffer }>;
                  }
                ).drafts ?? {};
              resolve(
                Object.values(drafts).some((d) =>
                  new TextDecoder().decode(d.data).includes("Family"),
                ),
              );
            };
          };
        }),
    );
    expect(plain).toBe(false);

    await context.setOffline(false);
    await page.reload();
    await expect(
      form.getByText(f("draftRestored"), { exact: true }),
    ).toBeVisible();
    await expect(form.getByLabel(f("start"))).toHaveValue(day(0));
    await expect(form.getByLabel(f("comment"))).toHaveValue("Family visit");
    await form.getByRole("button", { name: f("submit"), exact: true }).click();
    await expect(
      page.getByRole("status").filter({ hasText: /LV-\d{6}/ }),
    ).toBeVisible();
    await page.reload();
    await expect(form.getByLabel(f("start"))).toBeVisible();
    await expect(
      form.getByText(f("draftRestored"), { exact: true }),
    ).toHaveCount(0);
  });
});
