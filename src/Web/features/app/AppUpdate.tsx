"use client";

import { useEffect, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Icon } from "@/components/ui/Icon";
import { platform } from "@/lib/push";
import "./app.css";

/** True while the person is typing, so the prompt never shows over unsaved input (spec 030 FR-002). */
function typing(): boolean {
  const el = document.activeElement;
  if (!(el instanceof HTMLElement)) return false;
  return el.isContentEditable || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement || (el instanceof HTMLInputElement && !["button", "submit", "reset", "checkbox", "radio"].includes(el.type));
}

/** The release a service worker was registered for, from its `?v=` address. */
function versionOf(worker: ServiceWorker): string | null {
  try {
    return new URL(worker.scriptURL).searchParams.get("v");
  } catch {
    return null;
  }
}

const LATER_KEY = "tb.update.later";
/** How often an open page asks which release the server runs (spec 030 FR-002). */
export const RELEASE_CHECK_MS = 10 * 60 * 1000;

function laterFor(): string | null {
  try {
    return sessionStorage.getItem(LATER_KEY);
  } catch {
    return null;
  }
}

/**
 * Installs the service worker of this release and offers a reload only when the server runs a newer release than this
 * page (spec 030 US1, FR-002). A freshly loaded page is already the current release: a worker of the same release that
 * waits is activated silently, because nothing is open yet. The offer docks in the page flow above the shell (spec 036
 * DLS-078), stays hidden while a field has focus, and Later hides it for that release for the rest of the session.
 */
export function AppUpdate({ release }: { release: string }) {
  const t = useTranslations("app.update");
  const [waiting, setWaiting] = useState<ServiceWorker | null>(null);
  const [newer, setNewer] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [later, setLater] = useState<string | null>(null);
  const [hidden, setHidden] = useState(false);
  const reloading = useRef(false);

  useEffect(() => {
    if (!("serviceWorker" in navigator)) return;
    let registration: ServiceWorkerRegistration | undefined;
    const watch = (worker: ServiceWorker | null) => {
      if (!worker) return;
      const check = () => {
        if (worker.state !== "installed" || !navigator.serviceWorker.controller) return;
        const version = versionOf(worker);
        if (version === release) {
          // This page is already the release of the waiting worker: take it over now, without a prompt or a reload.
          worker.postMessage({ type: "SKIP_WAITING" });
          return;
        }
        // Another tab loaded a newer release and installed its worker.
        setWaiting(worker);
        if (version) setNewer(version);
      };
      check();
      worker.addEventListener("statechange", check);
    };
    const found = () => watch(registration?.installing ?? null);
    const changed = () => {
      if (!reloading.current) return;
      window.location.reload();
    };
    navigator.serviceWorker
      .register(`/sw.js?v=${encodeURIComponent(release)}`, { scope: "/" })
      .then((r) => {
        registration = r;
        watch(r.waiting);
        watch(r.installing);
        r.addEventListener("updatefound", found);
      })
      .catch(() => undefined);
    navigator.serviceWorker.addEventListener("controllerchange", changed);
    return () => {
      registration?.removeEventListener("updatefound", found);
      navigator.serviceWorker.removeEventListener("controllerchange", changed);
    };
  }, [release]);

  // A page left open across a deploy learns of the new release when it is shown again and every 10 minutes.
  useEffect(() => {
    if (release === "dev") return;
    let stopped = false;
    const ask = async () => {
      if (document.visibilityState !== "visible") return;
      try {
        const response = await fetch("/release", { cache: "no-store", credentials: "omit" });
        if (!response.ok) return;
        const body = (await response.json()) as { release?: unknown };
        if (!stopped && typeof body.release === "string" && body.release && body.release !== "dev" && body.release !== release) setNewer(body.release);
      } catch {
        // Offline or a deploy in progress: ask again later.
      }
    };
    const timer = window.setInterval(() => void ask(), RELEASE_CHECK_MS);
    document.addEventListener("visibilitychange", ask);
    return () => {
      stopped = true;
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", ask);
    };
  }, [release]);

  // The install is counted by platform family only, never a device (spec 030 section 13, mobile.install).
  useEffect(() => {
    const installed = () => {
      const body = JSON.stringify({ events: [{ name: "mobile.install", fields: { platform: platform() } }] });
      void fetch("/api/v1/analytics/events", { method: "POST", headers: { "content-type": "application/json" }, body, keepalive: true }).catch(() => undefined);
    };
    window.addEventListener("appinstalled", installed);
    return () => window.removeEventListener("appinstalled", installed);
  }, []);

  useEffect(() => {
    const update = () => setHidden(typing());
    // Focus moves to the next element after focusout, so look once it has landed.
    const left = () => setTimeout(update, 0);
    document.addEventListener("focusin", update);
    document.addEventListener("focusout", left);
    return () => {
      document.removeEventListener("focusin", update);
      document.removeEventListener("focusout", left);
    };
  }, []);

  // newer is only ever set in the browser, so session storage is read after hydration.
  if (!newer || newer === release || later === newer || laterFor() === newer || hidden) return null;
  return (
    <aside className="app-update-strip" role="status" aria-label={t("title")}>
      <Icon name="info" size={18} />
      <p className="app-update-title">{t("title")}</p>
      <div className="app-update-actions">
        <Button
          size="sm"
          variant="quiet"
          onClick={() => {
            setLater(newer);
            try {
              sessionStorage.setItem(LATER_KEY, newer);
            } catch {
              // Private mode: Later lasts for this page only.
            }
          }}
        >
          {t("later")}
        </Button>
        <Button
          size="sm"
          variant="primary"
          busy={busy}
          onClick={() => {
            setBusy(true);
            if (waiting) {
              reloading.current = true;
              waiting.postMessage({ type: "SKIP_WAITING" });
            } else {
              // The new page registers its own worker, which then takes over silently.
              window.location.reload();
            }
          }}
        >
          {t("reload")}
        </Button>
      </div>
    </aside>
  );
}
