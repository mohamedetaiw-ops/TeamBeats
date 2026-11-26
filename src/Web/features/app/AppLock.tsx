"use client";

import { useEffect, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { usePathname } from "next/navigation";
import { Button } from "@/components/ui/Button";
import { SettingsSection } from "@/components/ui/FormField";
import { Icon } from "@/components/ui/Icon";
import { Switch } from "@/components/ui/Switch";
import { chooseLock, inShell, lockChosen, onAppState, shellLock, verifyOnDevice, type ShellLock } from "@/lib/mobile/shell";
import "./app.css";

/** MOB-023: off never locks, optional only when the person turned it on, required always; after the set minutes away. */
function due(lock: ShellLock, chosen: boolean, awaySince: number | null, now: number): boolean {
  if (lock.appLock === "off" || (lock.appLock === "optional" && !chosen)) return false;
  return awaySince === null || now - awaySince >= lock.appLockMinutes * 60_000;
}

/** Pages that show nothing about anyone: the lock would only stand between the person and signing in. */
const OPEN_PAGES = ["/signin", "/app-start", "/offline"];

/**
 * The app lock of the store app (spec 030 R3 MOB-023, T017). In a browser it renders nothing. In the shell it covers
 * the page on start and when the app comes back after the set minutes, until the phone confirms the person with the
 * face, fingerprint or passcode. Unlocking never signs in and is never sent to the server; the session still ends by
 * the tenant's session rules.
 */
export function AppLock() {
  const t = useTranslations("app.lock");
  const [locked, setLocked] = useState(false);
  const [failed, setFailed] = useState(false);
  const settings = useRef<{ lock: ShellLock; chosen: boolean } | null>(null);
  const awaySince = useRef<number | null>(null);
  const pathname = usePathname();
  const open = OPEN_PAGES.some((p) => pathname === p || pathname.startsWith(`${p}/`));

  useEffect(() => {
    if (!inShell()) return;
    let stop: (() => void) | null = null;
    let live = true;
    void (async () => {
      const lock = await shellLock();
      if (!lock || !live) return;
      settings.current = { lock, chosen: await lockChosen() };
      if (due(lock, settings.current.chosen, null, Date.now())) setLocked(true);
      stop = await onAppState(async (active) => {
        if (!active) {
          awaySince.current = Date.now();
          return;
        }
        const current = settings.current;
        if (current && awaySince.current !== null) {
          current.chosen = await lockChosen();
          if (due(current.lock, current.chosen, awaySince.current, Date.now())) setLocked(true);
        }
        awaySince.current = null;
      });
    })();
    return () => {
      live = false;
      stop?.();
    };
  }, []);

  if (!locked || open) return null;
  return (
    <div className="app-lock" role="dialog" aria-modal="true" aria-labelledby="app-lock-title" aria-describedby="app-lock-message">
      <span className="ui-icon-tile" aria-hidden="true">
        <Icon name="lock" size={24} />
      </span>
      <h1 id="app-lock-title">{t("title")}</h1>
      <p id="app-lock-message">{failed ? t("failed") : t("message")}</p>
      <Button
        variant="primary"
        icon="lock"
        autoFocus
        onClick={async () => {
          const ok = await verifyOnDevice(t("reason"), t("cancel"));
          setFailed(!ok);
          if (ok) setLocked(false);
        }}
      >
        {t("unlock")}
      </Button>
    </div>
  );
}

/**
 * The person's own choice when the tenant leaves the app lock optional (MOB-023). Only in the store app, and only when
 * the lock is optional; a browser shows nothing.
 */
export function AppLockChoice() {
  const t = useTranslations("app.lock");
  const [state, setState] = useState<{ lock: ShellLock; chosen: boolean } | null>(null);

  useEffect(() => {
    if (!inShell()) return;
    let live = true;
    void (async () => {
      const lock = await shellLock();
      if (lock && live) setState({ lock, chosen: await lockChosen() });
    })();
    return () => {
      live = false;
    };
  }, []);

  if (!state || state.lock.appLock === "off") return null;
  return (
    <SettingsSection id="pref-app-lock" icon="lock" title={t("settingsTitle")} hint={t("settingsHint", { minutes: state.lock.appLockMinutes })}>
      {state.lock.appLock === "required" ? (
        <p className="field-hint">{t("required")}</p>
      ) : (
        <Switch
          label={t("choice")}
          checked={state.chosen}
          onChange={async (e) => {
            const on = e.target.checked;
            await chooseLock(on);
            setState({ ...state, chosen: on });
          }}
        />
      )}
    </SettingsSection>
  );
}
