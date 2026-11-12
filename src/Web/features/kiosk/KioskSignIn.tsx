"use client";

import { useEffect, useRef, useState, useSyncExternalStore, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Dialog } from "@/components/ui/Dialog";
import { FormField } from "@/components/ui/FormField";
import { kioskCall, validPin, type KioskDeviceInfo } from "@/lib/kiosk";
import { wipeDevice } from "./wipe";

/** The browser's barcode reader, where there is one (Chromium on Android and ChromeOS). Not in the TypeScript library yet. */
type Detector = { detect(source: HTMLVideoElement): Promise<{ rawValue: string }[]> };
type DetectorClass = new (options: { formats: string[] }) => Detector;

const recheckEvery = 30_000;

const noSubscription = () => () => {};
const cameraReadable = () => "BarcodeDetector" in window && !!navigator.mediaDevices?.getUserMedia;

/**
 * Badge and PIN sign-in on an enrolled kiosk (spec 030 US4 scenario 1, FR-015). A badge scanner types the code and
 * presses Enter; on a tablet with a camera the code can be read from the badge's QR too; the employee number works
 * once a PIN is set. A badge without a PIN chooses one here, typed twice. The kiosk checks its own enrolment every 30
 * seconds, so a revoked kiosk stops offering sign-in within a minute (scenario 5).
 */
export function KioskSignIn({ device }: { device: KioskDeviceInfo }) {
  const t = useTranslations("kiosk.signIn");
  const router = useRouter();
  const [badge, setBadge] = useState("");
  const [pin, setPin] = useState("");
  const [confirm, setConfirm] = useState("");
  const [choosing, setChoosing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [scanning, setScanning] = useState(false);
  const camera = useSyncExternalStore(noSubscription, cameraReadable, () => false);
  const pinRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    // The start screen keeps nothing from the person before: any session left is ended and the device is wiped.
    void fetch("/api/v1/auth/signout", { method: "POST" })
      .then((r) => r.text())
      .catch(() => "")
      .then(() => wipeDevice());
    const timer = window.setInterval(() => {
      void kioskCall<KioskDeviceInfo>("/api/v1/kiosk/device", "GET").then((r) => {
        if (r.ok && !r.value.enrolled) router.refresh();
      });
    }, recheckEvery);
    return () => window.clearInterval(timer);
  }, [router]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    if (choosing && (!validPin(pin) || pin !== confirm)) {
      setError(validPin(pin) ? t("pinMismatch") : t("pinFormat"));
      return;
    }
    setBusy(true);
    const result = await kioskCall<void>("/api/v1/kiosk/signin", "POST", { badge, pin, setPin: choosing });
    if (result.ok) {
      // A full load, so every part of the app starts with the new person's session.
      // eslint-disable-next-line @next/next/no-location-assign-relative-destination
      window.location.assign("/");
      return;
    }
    setBusy(false);
    if (result.refusal.key === "conflict.kiosk.pin_not_set") {
      setChoosing(true);
      setPin("");
      setConfirm("");
      setError(null);
      pinRef.current?.focus();
      return;
    }
    setPin("");
    setConfirm("");
    setError(result.refusal.messages[0] ?? t("failed"));
  }

  function restart() {
    setBadge("");
    setPin("");
    setConfirm("");
    setChoosing(false);
    setError(null);
  }

  return (
    <section className="kiosk-panel" aria-labelledby="kiosk-signin-title">
      <h2 id="kiosk-signin-title">{choosing ? t("chooseTitle") : t("title")}</h2>
      <p className="field-hint">{choosing ? t("chooseIntro") : t("intro")}</p>
      {error ? (
        <Alert tone="danger" title={t("refused")} live>
          {error}
        </Alert>
      ) : null}
      <form className="kiosk-form" onSubmit={(e) => void submit(e)} noValidate>
        <FormField
          label={t("badge")}
          name="badge"
          hint={t("badgeHint")}
          value={badge}
          onChange={(e) => setBadge(e.target.value)}
          autoComplete="off"
          spellCheck={false}
          autoCapitalize="characters"
          dir="ltr"
          readOnly={choosing}
          required
          autoFocus
        />
        {camera && !choosing ? (
          <Button type="button" variant="secondary" size="sm" onClick={() => setScanning(true)}>
            {t("scan")}
          </Button>
        ) : null}
        <FormField
          ref={pinRef}
          label={choosing ? t("newPin") : t("pin")}
          name="pin"
          type="password"
          inputMode="numeric"
          pattern="[0-9]*"
          maxLength={8}
          autoComplete="off"
          dir="ltr"
          hint={t("pinHint")}
          value={pin}
          onChange={(e) => setPin(e.target.value.replace(/\D/g, ""))}
          required
        />
        {choosing ? (
          <FormField
            label={t("confirmPin")}
            name="confirm"
            type="password"
            inputMode="numeric"
            pattern="[0-9]*"
            maxLength={8}
            autoComplete="off"
            dir="ltr"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value.replace(/\D/g, ""))}
            required
          />
        ) : null}
        <div className="kiosk-actions">
          {choosing ? (
            <Button type="button" variant="secondary" onClick={restart}>
              {t("back")}
            </Button>
          ) : null}
          <Button type="submit" busy={busy} disabled={badge.trim().length === 0 || pin.length === 0}>
            {choosing ? t("choose") : t("submit")}
          </Button>
        </div>
      </form>
      <p className="field-hint kiosk-device">
        {device.location ? t("deviceAt", { name: device.name ?? "", location: device.location }) : t("device", { name: device.name ?? "" })}
      </p>
      <p className="field-hint">{t("privacy", { minutes: device.idleMinutes })}</p>
      {scanning ? (
        <BadgeCamera
          onClose={() => setScanning(false)}
          onCode={(code) => {
            setBadge(code);
            setScanning(false);
            window.setTimeout(() => pinRef.current?.focus(), 0);
          }}
        />
      ) : null}
    </section>
  );
}

/** Reads the badge's QR with the tablet's camera. Nothing is recorded; the camera stops when the dialog closes. */
function BadgeCamera({ onClose, onCode }: { onClose: () => void; onCode: (code: string) => void }) {
  const t = useTranslations("kiosk.signIn");
  const video = useRef<HTMLVideoElement>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let stream: MediaStream | null = null;
    let timer = 0;
    let stopped = false;
    const Reader = (window as unknown as { BarcodeDetector?: DetectorClass }).BarcodeDetector;
    void navigator.mediaDevices
      .getUserMedia({ video: { facingMode: "environment" }, audio: false })
      .then(async (s) => {
        stream = s;
        if (stopped || !video.current || !Reader) return;
        video.current.srcObject = s;
        await video.current.play();
        const reader = new Reader({ formats: ["qr_code"] });
        const look = async () => {
          if (stopped || !video.current) return;
          const found = await reader.detect(video.current).catch(() => []);
          if (found[0]?.rawValue) onCode(found[0].rawValue);
          else timer = window.setTimeout(() => void look(), 250);
        };
        void look();
      })
      .catch(() => setFailed(true));
    return () => {
      stopped = true;
      window.clearTimeout(timer);
      stream?.getTracks().forEach((track) => track.stop());
    };
  }, [onCode]);

  return (
    <Dialog open onClose={onClose} title={t("scanTitle")} description={t("scanIntro")} closeLabel={t("scanClose")} size="sm">
      {failed ? <Alert tone="warning" title={t("cameraFailed")} /> : <video ref={video} className="kiosk-camera" muted playsInline aria-label={t("cameraLabel")} />}
    </Dialog>
  );
}
