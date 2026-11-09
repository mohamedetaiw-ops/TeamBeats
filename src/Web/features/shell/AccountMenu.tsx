"use client";

import { useEffect, useId, useRef, useState, type KeyboardEvent } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { SignOutButton } from "@/components/shell/SignOutButton";

/** Up to two initials of a display name, for the avatar. */
function initials(name: string): string {
  const words = name.replace(/\(.*?\)/g, " ").split(/\s+/).filter(Boolean);
  const letters = (words.length > 1 ? [words[0]!, words[words.length - 1]!] : words).map((w) => Array.from(w)[0] ?? "");
  return letters.join("").toLocaleUpperCase();
}

/**
 * The signed-in person in the shell (spec 008 FR-001, identity region): an avatar button that opens their name, a link
 * to their profile and sign out. Escape or a click outside closes it and returns focus to the avatar.
 */
export function AccountMenu({ name }: { name: string }) {
  const t = useTranslations("shell");
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const panelId = useId();

  useEffect(() => {
    const close = (e: PointerEvent) => {
      if (root.current && !root.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("pointerdown", close);
    return () => document.removeEventListener("pointerdown", close);
  }, []);

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === "Escape" && open) {
      setOpen(false);
      button.current?.focus();
    }
  }

  return (
    <div ref={root} className="shell-account" onKeyDown={onKeyDown}>
      <button
        ref={button}
        type="button"
        className="shell-avatar"
        aria-label={t("account", { name })}
        aria-expanded={open}
        aria-controls={panelId}
        onClick={() => setOpen((x) => !x)}
      >
        <span aria-hidden="true">{initials(name)}</span>
      </button>
      <div className="shell-account-panel" id={panelId} hidden={!open}>
        <bdi className="shell-user">{name}</bdi>
        <Link className="button button-quiet" href="/people/me">
          {t("profile")}
        </Link>
        <SignOutButton label={t("signOut")} />
      </div>
    </div>
  );
}
