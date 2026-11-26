"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import type { SiteGuestShare, SiteView } from "@/lib/content/types";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, SelectField } from "@/components/ui/FormField";
import { Widget } from "@/components/ui/Widget";
import "./content.css";

/**
 * Guests of a site (spec 009 R3 US10): the site shared read-only with guest spaces the person owns, for a number of
 * days. The space's guests read the site's published Public and Internal items in the space until the share ends.
 * The API checks the site, the space and the days again.
 */
export function SiteGuestShares({ site, shares, spaces }: { site: SiteView; shares: SiteGuestShare[]; spaces: { id: string; name: string }[] }) {
  const t = useTranslations("content.sites.guests");
  const format = useFormatter();
  const router = useRouter();
  const [space, setSpace] = useState(spaces[0]?.id ?? "");
  const [days, setDays] = useState("30");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const day = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium" });

  async function call(url: string, body?: unknown) {
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(url, {
        method: "POST",
        headers: body === undefined ? undefined : { "content-type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      router.refresh();
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  function share(e: FormEvent) {
    e.preventDefault();
    void call(`/api/v1/content/sites/${encodeURIComponent(site.id)}/guest-shares`, { spaceId: space, days: Number(days) });
  }

  return (
    <Widget title={t("title")}>
      <p className="field-hint">{t("intro")}</p>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {shares.length === 0 ? (
        <p className="field-hint">{t("none")}</p>
      ) : (
        <ul className="content-list" data-testid="site-guest-shares">
          {shares.map((s) => (
            <li key={s.id} className="content-actions">
              <bdi>{s.spaceName}</bdi>
              {s.live ? <Badge tone="success">{t("until", { date: day(s.expiresAt) })}</Badge> : <Badge tone="neutral">{t("ended")}</Badge>}
              {s.live ? (
                <ConfirmAction
                  label={t("revoke", { name: s.spaceName })}
                  question={t("revokeQuestion", { name: s.spaceName })}
                  confirmLabel={t("revokeConfirm")}
                  cancelLabel={t("cancel")}
                  onConfirm={() => call(`/api/v1/content/site-guest-shares/${encodeURIComponent(s.id)}/revoke`)}
                />
              ) : null}
            </li>
          ))}
        </ul>
      )}
      {site.status !== "active" ? (
        <p className="field-hint">{t("archived")}</p>
      ) : spaces.length === 0 ? (
        <p className="field-hint">{t("noSpaces")}</p>
      ) : (
        <form className="content-site-form" onSubmit={share}>
          <SelectField label={t("space")} name="guest-space" value={space} onChange={(e) => setSpace(e.target.value)} options={spaces.map((s) => ({ value: s.id, label: s.name }))} />
          <FormField label={t("days")} name="guest-days" type="number" min={1} max={365} value={days} onChange={(e) => setDays(e.target.value)} hint={t("daysHint")} required />
          <p className="content-actions">
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
              {t("share")}
            </Button>
          </p>
        </form>
      )}
    </Widget>
  );
}
