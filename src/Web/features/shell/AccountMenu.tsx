"use client";

import { useId, useRef, useState, type KeyboardEvent } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { SignOutButton } from "@/components/shell/SignOutButton";
import { Icon } from "@/components/ui/Icon";
import type { OwnProfile } from "@/lib/people/types";
import { useShellPanel } from "@/lib/shell/panel";

/** Up to two initials of a display name, for the avatar. */
function initials(name: string): string {
  const words = name.replace(/\(.*?\)/g, " ").split(/\s+/).filter(Boolean);
  const letters = (words.length > 1 ? [words[0]!, words[words.length - 1]!] : words).map((w) => Array.from(w)[0] ?? "");
  return letters.join("").toLocaleUpperCase();
}

/**
 * The signed-in person in the shell (spec 008 FR-001, identity region; spec 036 DLS-047): an avatar button that opens a
 * compact menu with who is signed in (name, job title, email), rows for My profile and Preferences, and Sign out after
 * a divider. Job title and email are read from the person's own profile when the menu first opens; without them the
 * header shows the name only. Escape, a press outside or opening another shell panel closes it.
 */
export function AccountMenu({ name }: { name: string }) {
  const t = useTranslations("shell");
  const root = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useShellPanel(root);
  const button = useRef<HTMLButtonElement>(null);
  const panelId = useId();
  const [details, setDetails] = useState<{ title: string | null; email: string | null } | null>(null);

  async function loadDetails() {
    if (details) return;
    try {
      const response = await fetch("/api/v1/me/profile", { cache: "no-store" });
      if (!response.ok) return setDetails({ title: null, email: null });
      const own = (await response.json()) as OwnProfile;
      const field = (key: string) => own.fields.find((f) => f.key === key)?.value ?? null;
      setDetails({ title: field("jobTitle"), email: field("email") });
    } catch {
      setDetails({ title: null, email: null });
    }
  }

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === "Escape" && open) {
      setOpen(false);
      button.current?.focus();
    }
  }

  const letters = initials(name);
  return (
    <div ref={root} className="shell-account" onKeyDown={onKeyDown}>
      <button
        ref={button}
        type="button"
        className="shell-avatar"
        aria-label={t("account", { name })}
        aria-expanded={open}
        aria-controls={panelId}
        onClick={() => {
          setOpen(!open);
          if (!open) void loadDetails();
        }}
      >
        <span aria-hidden="true">{letters}</span>
      </button>
      <div className="shell-account-panel shell-popover" id={panelId} hidden={!open}>
        <div className="shell-popover-body">
          <div className="shell-account-head">
            <span className="shell-account-avatar" aria-hidden="true">
              {letters}
            </span>
            <span className="shell-account-who">
              <bdi className="shell-user">{name}</bdi>
              {details?.title ? (
                <small>
                  <bdi>{details.title}</bdi>
                </small>
              ) : null}
              {details?.email ? (
                <small dir="ltr">
                  <bdi>{details.email}</bdi>
                </small>
              ) : null}
            </span>
          </div>
          <hr className="shell-popover-divider" />
          <Link className="shell-popover-row" href="/people/me">
            <Icon name="user" size={16} />
            {t("profile")}
          </Link>
          <Link className="shell-popover-row" href="/settings">
            <Icon name="settings" size={16} />
            {t("preferences")}
          </Link>
          <hr className="shell-popover-divider" />
          <SignOutButton className="shell-popover-row" label={t("signOut")} icon={<Icon name="arrow-forward" size={16} />} />
        </div>
      </div>
    </div>
  );
}
