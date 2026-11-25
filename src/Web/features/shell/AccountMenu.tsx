"use client";

import { useCallback, useRef, useState } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { SignOutButton } from "@/components/shell/SignOutButton";
import { Icon } from "@/components/ui/Icon";
import { PersonAvatar, PersonEntry } from "@/components/ui/Person";
import { Popover } from "@/components/ui/Popover";
import type { OwnProfile } from "@/lib/people/types";

/**
 * The signed-in person in the shell (spec 008 FR-001, identity region; spec 036 DLS-047): an avatar button that opens a
 * compact menu on the shared popover panel frame with who is signed in (name, job title, email), rows for My profile,
 * My payslips (when the API offers them) and Preferences, Administration for people the API says may open an admin
 * section (spec 008 FR-062; the sections still authorize themselves), and Sign out after a divider. Job title and email
 * are read from the person's own profile when the menu first opens; without them the header shows the name only.
 * Escape, a press outside or opening another shell panel closes it.
 */
export function AccountMenu({ name, administration = false }: { name: string; administration?: boolean }) {
  const t = useTranslations("shell");
  const [details, setDetails] = useState<{ title: string | null; email: string | null } | null>(null);
  const [payslips, setPayslips] = useState(false);
  const [open, setOpen] = useState(false);
  const loaded = useRef(false);

  const changed = useCallback((next: boolean) => {
    setOpen(next);
    if (!next || loaded.current) return;
    loaded.current = true;
    // My payslips shows when the API says the person has them (spec 019 US6); the page checks again.
    void fetch("/api/v1/me/payslips-available", { cache: "no-store" })
      .then(async (r) => (r.ok ? ((await r.json()) as { available: boolean }).available : false))
      .then(setPayslips, () => setPayslips(false));
    void fetch("/api/v1/me/profile", { cache: "no-store" })
      .then(async (response) => {
        if (!response.ok) return setDetails({ title: null, email: null });
        const own = (await response.json()) as OwnProfile;
        const field = (key: string) => own.fields.find((f) => f.key === key)?.value ?? null;
        setDetails({ title: field("jobTitle"), email: field("email") });
      })
      .catch(() => setDetails({ title: null, email: null }));
  }, []);

  return (
    <div className="shell-account">
      <Popover
        label={t("account", { name })}
        iconOnly
        face={<PersonAvatar name={name} tone="solid" />}
        frame="panel"
        list
        placement="bottom-end"
        testId="shell-account-panel"
        triggerTestId="shell-account"
        onOpenChange={changed}
      >
        {/* The rows render only while the menu is open, so a closed menu adds no names or links to the page. */}
        {open ? (
          <>
        <PersonEntry
          person={{
            name,
            href: "/people/me",
            avatar: { tone: "solid" },
            title: details?.title ? { text: details.title } : null,
            meta: details?.email ?? null,
          }}
        />
        <hr className="ui-menu-separator" />
        <Link className="ui-menu-item" href="/people/me">
          <Icon name="user" size={16} />
          {t("profile")}
        </Link>
        {payslips ? (
          <Link className="ui-menu-item" href="/payslips">
            <Icon name="files" size={16} />
            {t("payslips")}
          </Link>
        ) : null}
        <Link className="ui-menu-item" href="/settings">
          <Icon name="settings" size={16} />
          {t("preferences")}
        </Link>
        {administration ? (
          <Link className="ui-menu-item" href="/admin">
            <Icon name="lock" size={16} />
            {t("administration")}
          </Link>
        ) : null}
        <hr className="ui-menu-separator" />
        <SignOutButton className="ui-menu-item" label={t("signOut")} icon={<Icon name="arrow-forward" size={16} />} />
          </>
        ) : null}
      </Popover>
    </div>
  );
}
