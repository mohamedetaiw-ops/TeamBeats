import { test } from "node:test";
import assert from "node:assert/strict";
import { effectiveLock, route, shouldLock, workspaceHost, workspaceLocked } from "../src/rules.ts";

const start = "https://teambeats.app/app-start";

test("a workspace host is a plain DNS name", () => {
  assert.equal(workspaceHost("Contoso.TeamBeats.app"), "contoso.teambeats.app");
  assert.equal(workspaceHost(" https://intranet.contoso.com/home?x=1 "), "intranet.contoso.com");
  assert.equal(workspaceHost("intranet.contoso.com."), "intranet.contoso.com");
  for (const bad of ["http://contoso.teambeats.app", "contoso", "contoso.teambeats.app:8443", "me@contoso.com", "10.0.0.1", "-bad.example.com", "", 42, null]) {
    assert.equal(workspaceHost(bad), null, String(bad));
  }
});

test("only the workspace, its sign-in hosts and the start page load in the app", () => {
  const hosts = ["login.microsoftonline.com"];
  assert.equal(route("https://contoso.teambeats.app/home", "contoso.teambeats.app", hosts, start, false), "app");
  assert.equal(route("https://login.microsoftonline.com/x/oauth2/v2.0/authorize", "contoso.teambeats.app", hosts, start, false), "app");
  assert.equal(route("https://teambeats.app/app-start?again=1", "contoso.teambeats.app", hosts, start, false), "app");
  assert.equal(route("https://example.com/", "contoso.teambeats.app", hosts, start, false), "browser");
  assert.equal(route("http://contoso.teambeats.app/", "contoso.teambeats.app", hosts, start, false), "browser");
  assert.equal(route("https://contoso.teambeats.app:8443/", "contoso.teambeats.app", hosts, start, false), "browser");
  assert.equal(route("https://evil.contoso.teambeats.app/", "contoso.teambeats.app", hosts, start, false), "browser");
  assert.equal(route("https://teambeats.app/other", "contoso.teambeats.app", hosts, start, false), "browser");
  assert.equal(route("not a url", "contoso.teambeats.app", hosts, start, false), "browser");
  // A workspace locked by MDM never offers the start page.
  assert.equal(route("https://teambeats.app/app-start", "contoso.teambeats.app", hosts, start, true), "browser");
  // Before a workspace is chosen, only the start page loads.
  assert.equal(route("https://login.microsoftonline.com/", null, hosts, start, false), "browser");
});

test("the managed configuration can only make the lock stricter", () => {
  const tenant = { appLock: "optional", appLockMinutes: 5 } as const;
  assert.deepEqual(effectiveLock(tenant, {}), { appLock: "optional", appLockMinutes: 5 });
  assert.deepEqual(effectiveLock(tenant, { appLock: "required", appLockMinutes: 1 }), { appLock: "required", appLockMinutes: 1 });
  assert.deepEqual(effectiveLock(tenant, { appLock: "off", appLockMinutes: 30 }), { appLock: "optional", appLockMinutes: 5 });
  assert.deepEqual(effectiveLock(tenant, { appLock: "sometimes", appLockMinutes: "0" }), { appLock: "optional", appLockMinutes: 0 });
  assert.deepEqual(effectiveLock({ appLock: "required", appLockMinutes: 0 }, { appLock: "optional", appLockMinutes: 61 }), { appLock: "required", appLockMinutes: 0 });
  assert.equal(workspaceLocked({ lockWorkspace: "true" }), true);
  assert.equal(workspaceLocked({ lockWorkspace: "yes" }), false);
});

test("the lock follows the policy, the person's choice and the time away", () => {
  const now = 10 * 60_000;
  assert.equal(shouldLock({ appLock: "off", appLockMinutes: 0 }, true, 0, now), false);
  assert.equal(shouldLock({ appLock: "optional", appLockMinutes: 5 }, false, 0, now), false);
  assert.equal(shouldLock({ appLock: "optional", appLockMinutes: 5 }, true, now - 4 * 60_000, now), false);
  assert.equal(shouldLock({ appLock: "optional", appLockMinutes: 5 }, true, now - 5 * 60_000, now), true);
  assert.equal(shouldLock({ appLock: "required", appLockMinutes: 0 }, false, now, now), true);
  assert.equal(shouldLock({ appLock: "required", appLockMinutes: 30 }, false, null, now), true);
});
