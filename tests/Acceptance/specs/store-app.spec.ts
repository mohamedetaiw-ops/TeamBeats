import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 030 R3 User Story 6 (T016, T017): the store app's start page, and the web app inside the shell. The shell is
 * stood in for by a fake Capacitor bridge on the page: the lock covers the page until the phone confirms the person,
 * comes back after the app was away, and native push registers the phone's token with the API.
 */
test.describe("store app", () => {
  let tenant: TestTenant;

  test.beforeAll(async () => {
    tenant = await createTenant("shell", "en", "enterprise_gov");
  });

  test("the start page asks for a workspace address and says why the app came back", async ({ page }, testInfo) => {
    const s = (key: string) => msg("en", `app.start.${key}`);
    await page.goto(`${hostOrigin(tenant.key)}/app-start?shell=not_available`);
    await expect(page.getByRole("heading", { name: s("title"), level: 1 })).toBeVisible();
    await expect(page.getByText(s("problems.not_available"), { exact: true })).toBeVisible();
    await page.getByLabel(s("workspace")).fill("not a workspace");
    await page.getByRole("button", { name: s("open"), exact: true }).click();
    await expect(page.getByText(s("invalid"), { exact: true })).toBeVisible();
    await expectAccessible(page, testInfo, "app-start");
  });

  test("in the shell the lock covers the page and native push registers the phone", async ({ page }, testInfo) => {
    await page.addInitScript(() => {
      const store = new Map<string, string>([["lock", JSON.stringify({ appLock: "required", appLockMinutes: 0 })]]);
      const listeners = new Map<string, ((data: Record<string, unknown>) => void)[]>();
      const on = (event: string, handler: (data: Record<string, unknown>) => void) => {
        listeners.set(event, [...(listeners.get(event) ?? []), handler]);
        return Promise.resolve({ remove: async () => listeners.set(event, (listeners.get(event) ?? []).filter((h) => h !== handler)) });
      };
      const emit = (event: string, data: Record<string, unknown>) => (listeners.get(event) ?? []).forEach((h) => h(data));
      let permission = "prompt";
      (window as unknown as { __shell: unknown }).__shell = { emit, store };
      (window as unknown as { Capacitor: unknown }).Capacitor = {
        isNativePlatform: () => true,
        getPlatform: () => "android",
        Plugins: {
          Preferences: {
            get: async ({ key }: { key: string }) => ({ value: store.get(key) ?? null }),
            set: async ({ key, value }: { key: string; value: string }) => void store.set(key, value),
            remove: async ({ key }: { key: string }) => void store.delete(key),
          },
          App: { addListener: on },
          BiometricAuthNative: { authenticate: async () => undefined },
          PushNotifications: {
            addListener: on,
            checkPermissions: async () => ({ receive: permission }),
            requestPermissions: async () => ({ receive: (permission = "granted") }),
            register: async () => setTimeout(() => emit("registration", { value: "e2e" + "a1b2c3d4".repeat(8) + ":APA91b" }), 10),
            unregister: async () => undefined,
          },
        },
      };
    });
    const l = (key: string) => msg("en", `app.lock.${key}`);
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    const lock = page.getByRole("dialog", { name: l("title") });
    await expect(lock).toBeVisible();
    await expectAccessible(page, testInfo, "app-lock");
    await lock.getByRole("button", { name: l("unlock"), exact: true }).click();
    await expect(lock).toBeHidden();

    // Away and back: locked again, since the policy locks at once.
    await page.evaluate(() => {
      const shell = (window as unknown as { __shell: { emit(e: string, d: Record<string, unknown>): void } }).__shell;
      shell.emit("appStateChange", { isActive: false });
      shell.emit("appStateChange", { isActive: true });
    });
    await expect(lock).toBeVisible();
    await lock.getByRole("button", { name: l("unlock"), exact: true }).click();
    await expect(lock).toBeHidden();

    // Native push from the notification settings, registered with the API.
    await page.goto(`${hostOrigin(tenant.key)}/notifications/preferences`);
    await page.getByRole("dialog", { name: l("title") }).getByRole("button", { name: l("unlock"), exact: true }).click();
    const m = (key: string) => msg("en", `notifications.preferences.${key}`);
    await expect(page.getByText(m("pushIsOff"), { exact: true })).toBeVisible();
    await page.getByRole("button", { name: m("pushTurnOn"), exact: true }).click();
    await expect(page.getByRole("status").getByText(m("pushTurnedOn"), { exact: true })).toBeVisible();
    const devices = () => page.evaluate(async () => ((await (await fetch("/api/v1/me/push")).json()) as { devices: number }).devices);
    expect(await devices()).toBe(1);
    await page.getByRole("button", { name: m("pushTurnOff"), exact: true }).click();
    await expect(page.getByRole("status").getByText(m("pushTurnedOff"), { exact: true })).toBeVisible();
    expect(await devices()).toBe(0);
  });
});
