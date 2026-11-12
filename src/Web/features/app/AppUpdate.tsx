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

/**
 * Installs the service worker of this release and offers a new release when one is waiting (spec 030 US1, FR-002). A
 * new version is applied only when the person taps Reload; the prompt stays hidden while a field has focus.
 */
export function AppUpdate({ release }: { release: string }) {
  const t = useTranslations("app.update");
  const [waiting, setWaiting] = useState<ServiceWorker | null>(null);
  const [busy, setBusy] = useState(false);
  const [later, setLater] = useState(false);
  const [hidden, setHidden] = useState(false);
  const reloading = useRef(false);

  useEffect(() => {
    if (!("serviceWorker" in navigator)) return;
    let registration: ServiceWorkerRegistration | undefined;
    const watch = (worker: ServiceWorker | null) => {
      if (!worker) return;
      const check = () => {
        if (worker.state === "installed" && navigator.serviceWorker.controller) {
          setLater(false);
          setWaiting(worker);
        }
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
        r.addEventListener("updatefound", found);
      })
      .catch(() => undefined);
    navigator.serviceWorker.addEventListener("controllerchange", changed);
    return () => {
      registration?.removeEventListener("updatefound", found);
      navigator.serviceWorker.removeEventListener("controllerchange", changed);
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

  if (!waiting || later || hidden) return null;
  return (
    <aside className="app-update" role="status" aria-label={t("title")}>
      <Icon name="spark" size={18} />
      <p className="app-update-title">{t("title")}</p>
      <div className="app-update-actions">
        <Button size="sm" variant="quiet" onClick={() => setLater(true)}>
          {t("later")}
        </Button>
        <Button
          size="sm"
          variant="primary"
          busy={busy}
          onClick={() => {
            setBusy(true);
            reloading.current = true;
            waiting.postMessage({ type: "SKIP_WAITING" });
          }}
        >
          {t("reload")}
        </Button>
      </div>
    </aside>
  );
}
