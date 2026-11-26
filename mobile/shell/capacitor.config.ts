import type { CapacitorConfig } from "@capacitor/cli";

/**
 * The TeamBeats store app (ADR-0023). The shell starts on its own page in www, which finds the workspace and opens it;
 * the ShellGuard plugin decides every later navigation (MOB-021), so no host is allowed here.
 */
const config: CapacitorConfig = {
  appId: process.env.TEAMBEATS_APP_ID ?? "app.teambeats.shell",
  appName: process.env.TEAMBEATS_APP_NAME ?? "TeamBeats",
  webDir: "www",
  server: { androidScheme: "https", allowNavigation: [] },
  android: { allowMixedContent: false, webContentsDebuggingEnabled: false },
  ios: { limitsNavigationsToAppBoundDomains: false, contentInset: "automatic" },
  plugins: {
    CapacitorHttp: { enabled: false },
    PushNotifications: { presentationOptions: ["badge", "sound", "alert"] },
  },
};

export default config;
