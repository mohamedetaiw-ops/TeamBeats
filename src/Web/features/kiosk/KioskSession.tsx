"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Dialog } from "@/components/ui/Dialog";
import { activityEvents } from "@/lib/kiosk";
import { endKioskSession } from "./wipe";
import "./kiosk.css";

/** How often use is reported to the server while the person is active, so its own idle timer agrees with this one. */
const reportEvery = 20_000;
const warnBefore = 30_000;

/**
 * A session on a shared kiosk (spec 030 US4 scenario 2, FR-016). After the kiosk's idle time (2 minutes unless the
 * administrator chose 1 to 15) the person is signed out and the device is wiped; 30 seconds before, a dialog offers to
 * keep going. The bar says the device is shared and signs out at once. The server ends the session on the same timer.
 */
export function KioskSession({ idleMinutes }: { idleMinutes: number }) {
  const t = useTranslations("kiosk.session");
  const [left, setLeft] = useState<number | null>(null);
  const [ending, setEnding] = useState(false);

  useEffect(() => {
    const limit = idleMinutes * 60_000;
    const warnAt = Math.max(limit - warnBefore, limit / 2);
    let last = Date.now();
    let reported = Date.now();
    const active = () => {
      last = Date.now();
      setLeft(null);
      if (last - reported > reportEvery) {
        reported = last;
        void fetch("/api/v1/kiosk/device", { cache: "no-store" })
          .then((r) => r.text())
          .catch(() => "");
      }
    };
    activityEvents.forEach((name) => window.addEventListener(name, active, { passive: true, capture: true }));
    const timer = window.setInterval(() => {
      const idle = Date.now() - last;
      if (idle >= limit) {
        window.clearInterval(timer);
        setEnding(true);
        void endKioskSession();
      } else if (idle >= warnAt) {
        setLeft(Math.ceil((limit - idle) / 1000));
      }
    }, 1000);
    return () => {
      window.clearInterval(timer);
      activityEvents.forEach((name) => window.removeEventListener(name, active, { capture: true }));
    };
  }, [idleMinutes]);

  return (
    <>
      <div className="kiosk-bar" role="region" aria-label={t("label")}>
        <p>{t("bar", { minutes: idleMinutes })}</p>
        <Button
          size="sm"
          variant="secondary"
          busy={ending}
          onClick={() => {
            setEnding(true);
            void endKioskSession();
          }}
        >
          {t("signOut")}
        </Button>
      </div>
      <Dialog
        open={left !== null && !ending}
        onClose={() => setLeft(null)}
        title={t("warningTitle")}
        description={t("warning", { seconds: left ?? 0 })}
        closeLabel={t("keepGoing")}
        size="sm"
        footer={
          <>
            <Button
              variant="secondary"
              onClick={() => {
                setEnding(true);
                void endKioskSession();
              }}
            >
              {t("signOut")}
            </Button>
            <Button onClick={() => setLeft(null)}>{t("keepGoing")}</Button>
          </>
        }
      />
    </>
  );
}
