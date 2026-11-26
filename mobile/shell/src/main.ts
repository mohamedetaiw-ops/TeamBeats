import { effectiveLock, workspaceHost, workspaceLocked, type Managed, type ShellConfig } from "./rules.ts";

/**
 * The store app's start (spec 030 R3, ADR-0023). It has no product pages: it finds the workspace (from MDM, from the
 * last one used, or from the platform start page), asks that workspace what the app may do, tells the navigation
 * guard which hosts load in the app, and opens the workspace. Plugins are reached through the bridge Capacitor puts
 * on the page, so this file needs no bundler.
 */

type Plugin = Record<string, (options?: Record<string, unknown>) => Promise<Record<string, unknown>>>;

declare global {
  interface Window {
    Capacitor?: { Plugins: Record<string, Plugin | undefined>; isNativePlatform(): boolean };
  }
}

/** The platform start page of the web app, set at build time from TEAMBEATS_START_URL (see capacitor.config.ts). */
const START_URL = (document.documentElement.dataset.startUrl ?? "https://teambeats.app/app-start").replace(/\/$/, "");

function plugin(name: string): Plugin {
  const found = window.Capacitor?.Plugins[name];
  if (!found) throw new Error(`The ${name} plugin is missing from this build.`);
  return found;
}

async function managed(): Promise<Managed> {
  try {
    const keys = ["workspaceHost", "lockWorkspace", "appLock", "appLockMinutes"];
    const entries = await Promise.all(keys.map(async (key) => [key, (await plugin("ManagedConfigurations").getString({ key })).value] as const));
    return Object.fromEntries(entries.filter(([, v]) => v !== null && v !== undefined));
  } catch {
    return {};
  }
}

async function shellConfig(host: string): Promise<ShellConfig | null> {
  try {
    const response = await plugin("CapacitorHttp").get({ url: `https://${host}/api/v1/mobile/shell`, headers: { accept: "application/json" } });
    return response.status === 200 ? (response.data as ShellConfig) : null;
  } catch {
    return null;
  }
}

function toStart(reason: string | null): void {
  window.location.replace(reason ? `${START_URL}?shell=${encodeURIComponent(reason)}` : START_URL);
}

async function run(): Promise<void> {
  const config = await managed();
  const locked = workspaceLocked(config);
  const preferences = plugin("Preferences");
  // A workspace chosen on the start page arrives as ?workspace= on the shell's own page.
  const chosen = new URLSearchParams(window.location.search).get("workspace");
  const host =
    workspaceHost(config.workspaceHost) ?? (locked ? null : workspaceHost(chosen) ?? workspaceHost((await preferences.get({ key: "workspace" })).value));
  if (host === null) {
    toStart(locked ? "managed_invalid" : null);
    return;
  }
  const answer = await shellConfig(host);
  if (answer === null || !answer.available) {
    if (!locked) await preferences.remove({ key: "workspace" });
    toStart(answer === null ? "unreachable" : "not_available");
    return;
  }
  await preferences.set({ key: "workspace", value: host });
  // The web app applies the lock; it reads the managed values through the same rule.
  await preferences.set({ key: "lock", value: JSON.stringify(effectiveLock(answer, config)) });
  await plugin("ShellGuard").configure({ workspace: host, signInHosts: answer.signInHosts, startUrl: START_URL, locked });
  window.location.replace(`https://${host}/`);
}

void run().catch(() => toStart("unreachable"));
