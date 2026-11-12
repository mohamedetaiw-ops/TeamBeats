"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Icon } from "@/components/ui/Icon";
import { declinedRecently, meaningfulActionEvent, pushSupported, rememberDecline, turnOnPush, type PushStatus } from "@/lib/push";
import "./app.css";

/**
 * The push offer (spec 030 US2 scenario 1, FR-006). It never shows on a first visit: only after a meaningful action, such
 * as a submitted leave or service request, announces itself with the shared event. It asks at most once per page load,
 * only while the browser has not been asked yet, and "Not now" keeps it away from this device for 90 days. The browser's
 * own permission prompt appears only after the person taps "Turn on".
 */
export function PushOffer({ owner }: { owner: string }) {
  const t = useTranslations("app.push");
  const [status, setStatus] = useState<PushStatus | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let asked = false;
    const offer = () => {
      if (asked || !pushSupported() || Notification.permission !== "default" || declinedRecently(owner)) return;
      asked = true;
      void fetch("/api/v1/me/push")
        .then((r) => (r.ok ? (r.json() as Promise<PushStatus>) : r.text().then(() => null)))
        .then((s) => {
          if (s?.available && s.publicKey) setStatus(s);
        })
        .catch(() => undefined);
    };
    window.addEventListener(meaningfulActionEvent, offer);
    return () => window.removeEventListener(meaningfulActionEvent, offer);
  }, [owner]);

  if (!status) return null;
  return (
    <aside className="app-update push-offer" role="region" aria-label={t("title")}>
      <Icon name="bell" size={18} />
      <div className="app-update-title">
        <p className="push-offer-title">{t("title")}</p>
        <p className="field-hint">{t("message")}</p>
      </div>
      <div className="app-update-actions">
        <Button
          size="sm"
          variant="quiet"
          onClick={() => {
            rememberDecline(owner);
            setStatus(null);
          }}
        >
          {t("notNow")}
        </Button>
        <Button
          size="sm"
          variant="primary"
          busy={busy}
          onClick={async () => {
            setBusy(true);
            const outcome = await turnOnPush(status.publicKey!);
            // A browser refusal is the browser's to remember; a failed request may be offered again later.
            if (outcome === "denied") rememberDecline(owner);
            setBusy(false);
            setStatus(null);
          }}
        >
          {t("turnOn")}
        </Button>
      </div>
    </aside>
  );
}
