"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import type { ScreenView, ScreenWithAddress, SiteView } from "@/lib/content/types";
import "./content.css";

/**
 * Digital signage screens of a site (spec 009 section 4): each screen has a read-only address that shows the site's
 * Public items meant for everyone. The address is shown once, when the screen is added or its address is replaced;
 * replacing it or removing the screen stops the old one.
 */
export function ScreenSettings({ site, screens }: { site: SiteView; screens: ScreenView[] }) {
  const t = useTranslations("content.sites.screens");
  const router = useRouter();
  const [name, setName] = useState("");
  const [address, setAddress] = useState<{ name: string; url: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function call(url: string, method: "POST" | "DELETE", body?: unknown) {
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(url, {
        method,
        headers: body === undefined ? undefined : { "content-type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      if (response.status !== 204) {
        const shown = (await response.json()) as ScreenWithAddress;
        setAddress({ name: shown.screen.name, url: window.location.origin + shown.path });
      } else {
        setAddress(null);
      }
      router.refresh();
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  async function add(event: React.FormEvent) {
    event.preventDefault();
    await call(`/api/v1/content/sites/${encodeURIComponent(site.id)}/screens`, "POST", { name: name.trim() });
    setName("");
  }

  return (
    <section className="content-card" aria-labelledby="site-screens">
      <h2 id="site-screens">{t("title")}</h2>
      <p className="field-hint">{t("intro")}</p>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {address ? (
        <div className="notice notice-success" role="status" data-testid="screen-address">
          <p>{t("address", { name: address.name })}</p>
          <p>
            <bdi dir="ltr">{address.url}</bdi>
          </p>
        </div>
      ) : null}
      {screens.length === 0 ? (
        <p className="field-hint">{t("none")}</p>
      ) : (
        <ul className="content-list">
          {screens.map((s) => (
            <li key={s.id} className="content-actions">
              <bdi>{s.name}</bdi>
              <Button busy={busy} busyLabel={t("working")} onClick={() => void call(`/api/v1/content/screens/${encodeURIComponent(s.id)}/rotate`, "POST")}>
                {t("rotate", { name: s.name })}
              </Button>
              <ConfirmAction
                label={t("remove", { name: s.name })}
                question={t("removeQuestion", { name: s.name })}
                confirmLabel={t("removeConfirm")}
                cancelLabel={t("cancel")}
                onConfirm={() => call(`/api/v1/content/screens/${encodeURIComponent(s.id)}`, "DELETE")}
              />
            </li>
          ))}
        </ul>
      )}
      {site.status === "active" ? (
        <form className="content-site-form" onSubmit={(e) => void add(e)}>
          <FormField label={t("name")} name="screen-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} />
          <p className="content-actions">
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
              {t("add")}
            </Button>
          </p>
        </form>
      ) : null}
    </section>
  );
}
