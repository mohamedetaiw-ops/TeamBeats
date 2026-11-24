"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, SelectField } from "@/components/ui/FormField";
import { Widget } from "@/components/ui/Widget";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { problemMessages, type SignageScreen, type SignageScreenAddress } from "@/lib/notifications/types";
import "./notifications.css";

/**
 * Signage screens of a location (spec 015 US7, FR-021). A location administrator registers a screen at an org unit and,
 * optionally, the location name people's profiles use. The screen's address is shown once; replacing it or revoking the
 * screen stops the old one within a minute. Which screens and units are listed, and every change, is decided by the API.
 */
export function SignageAdmin({ initial, units }: { initial: SignageScreen[]; units: OrgUnit[] }) {
  const t = useTranslations("notifications.signage");
  const locale = useLocale();
  const router = useRouter();
  const [screens, setScreens] = useState(initial);
  const [address, setAddress] = useState<{ name: string; url: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [name, setName] = useState("");
  const [unit, setUnit] = useState(units[0]?.id ?? "");
  const [location, setLocation] = useState("");

  const when = (iso: string | null) => (iso ? new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(iso)) : t("never"));
  const unitLabel = (id: string) => {
    const u = units.find((x) => x.id === id);
    return u ? unitName(u, locale) : t("otherUnit");
  };

  async function call(url: string, method: "POST" | "DELETE", body?: unknown): Promise<boolean> {
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
        return false;
      }
      if (response.status === 204) {
        setAddress(null);
      } else {
        const shown = (await response.json()) as SignageScreenAddress;
        setAddress({ name: shown.screen.name, url: window.location.origin + shown.path });
        setScreens((list) => [...list.filter((s) => s.id !== shown.screen.id), shown.screen].sort((a, b) => a.name.localeCompare(b.name)));
      }
      router.refresh();
      return true;
    } catch {
      setMessages([t("failed")]);
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function add(event: FormEvent) {
    event.preventDefault();
    const ok = await call("/api/v1/notifications/signage-screens", "POST", { name: name.trim(), orgUnitId: unit, location: location.trim() || null });
    if (ok) {
      setName("");
      setLocation("");
    }
  }

  async function revoke(screen: SignageScreen) {
    if (await call(`/api/v1/notifications/signage-screens/${encodeURIComponent(screen.id)}`, "DELETE")) {
      setScreens((list) => list.filter((s) => s.id !== screen.id));
    }
  }

  return (
    <div className="notification-signage">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </Alert>
      ) : null}
      {address ? (
        <Alert tone="success" title={t("address", { name: address.name })} live action={<Button size="sm" variant="secondary" onClick={() => setAddress(null)}>{t("addressDone")}</Button>}>
          <p data-testid="screen-address">
            <bdi dir="ltr">{address.url}</bdi>
          </p>
          <p>{t("addressOnce")}</p>
        </Alert>
      ) : null}
      <Widget title={t("listTitle")}>
        <DataTable
          caption={t("listTitle")}
          captionHidden
          rows={screens}
          rowKey={(s) => s.id}
          emptyTitle={t("emptyTitle")}
          emptyMessage={t("emptyMessage")}
          columns={[
            { key: "name", header: t("name"), rowHeader: true, cell: (s) => <bdi>{s.name}</bdi>, sortValue: (s) => s.name },
            { key: "unit", header: t("unit"), cell: (s) => <bdi>{unitLabel(s.orgUnitId)}</bdi>, sortValue: (s) => unitLabel(s.orgUnitId) },
            { key: "location", header: t("location"), cell: (s) => (s.location ? <bdi>{s.location}</bdi> : t("anyLocation")) },
            { key: "seen", header: t("lastSeen"), nowrap: true, priority: "low", cell: (s) => when(s.lastSeenAt) },
            {
              key: "actions",
              header: t("actions"),
              cell: (s) => (
                <span className="notification-signage-actions">
                  <Button size="sm" variant="secondary" busy={busy} busyLabel={t("working")}
                    onClick={() => void call(`/api/v1/notifications/signage-screens/${encodeURIComponent(s.id)}:rotate`, "POST")}>
                    {t("rotate", { name: s.name })}
                  </Button>
                  <ConfirmAction
                    label={t("revoke", { name: s.name })}
                    question={t("revokeQuestion", { name: s.name })}
                    confirmLabel={t("revokeConfirm")}
                    cancelLabel={t("cancel")}
                    onConfirm={() => revoke(s)}
                  />
                </span>
              ),
            },
          ]}
        />
      </Widget>
      <Widget title={t("createTitle")}>
        <p className="field-hint">{t("createIntro")}</p>
        <form className="notification-signage-form" onSubmit={(e) => void add(e)} noValidate>
          <FormField label={t("name")} name="screen-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} />
          <SelectField
            label={t("unit")}
            name="screen-unit"
            hint={t("unitHint")}
            value={unit}
            onChange={(e) => setUnit(e.target.value)}
            options={units.map((u) => ({ value: u.id, label: unitName(u, locale) }))}
          />
          <FormField label={t("location")} name="screen-location" hint={t("locationHint")} value={location} onChange={(e) => setLocation(e.target.value)} maxLength={100} />
          <p className="notification-signage-actions">
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")} disabled={name.trim().length === 0 || !unit}>
              {t("create")}
            </Button>
          </p>
        </form>
      </Widget>
    </div>
  );
}
