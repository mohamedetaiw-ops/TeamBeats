"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { bookingApi, call, pick, send, type ResourceView, type SiteSensors as SiteSensorsView } from "@/lib/booking";
import "./booking.css";

/**
 * Occupancy sensors of a site (spec 031 R3 US9, FR-026): facilities administrators type the sensor ID of each resource
 * for one occupancy connection; an empty ID takes the sensor off. The last state each sensor reported is shown. The
 * API checks the connection, the IDs and the site.
 */
export function SiteSensors({ siteId, resources, initial }: { siteId: string; resources: ResourceView[]; initial: SiteSensorsView }) {
  const t = useTranslations("booking.sensors");
  const locale = useLocale();
  const format = useFormatter();
  const [view, setView] = useState(initial);
  const [connection, setConnection] = useState(initial.connections[0]?.id ?? "");
  const idsFor = (v: SiteSensorsView, c: string) => Object.fromEntries(resources.map((r) => [r.id, v.sensors.find((s) => s.connectionId === c && s.resourceId === r.id)?.sensorId ?? ""]));
  const [ids, setIds] = useState<Record<string, string>>(() => idsFor(initial, initial.connections[0]?.id ?? ""));
  const [messages, setMessages] = useState<string[]>([]);
  const [done, setDone] = useState(false);
  const [busy, setBusy] = useState(false);

  async function save() {
    setBusy(true);
    setDone(false);
    const mappings = Object.entries(ids).filter(([, sensorId]) => sensorId.trim() !== "").map(([resourceId, sensorId]) => ({ resourceId, sensorId: sensorId.trim() }));
    const result = await call<SiteSensorsView>(() => send(`${bookingApi}/manage/sites/${siteId}/sensors`, "PUT", { connectionId: connection, mappings }), t("failed"));
    setBusy(false);
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setView(result.value);
    setIds(idsFor(result.value, connection));
    setDone(true);
  }

  return (
    <section className="booking-section" aria-labelledby="booking-sensors">
      <h2 id="booking-sensors">{t("title")}</h2>
      <p className="booking-meta">{view.sensorCheckIn ? t("checkInOn") : t("checkInOff")}</p>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {done ? <Alert tone="success" title={t("saved")} live /> : null}
      {view.connections.length === 0 ? (
        <StateView kind="empty" title={t("noConnectionsTitle")} message={t("noConnections")} icon="link" />
      ) : resources.length === 0 ? (
        <StateView kind="empty" title={t("noResourcesTitle")} message={t("noResources")} />
      ) : (
        <form
          className="booking-form"
          aria-label={t("title")}
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          {view.connections.length > 1 ? (
            <SelectField label={t("connection")} name="connection" value={connection}
              onChange={(e) => {
                setConnection(e.target.value);
                setIds(idsFor(view, e.target.value));
              }}
              options={view.connections.map((c) => ({ value: c.id, label: c.name }))} />
          ) : null}
          <ul className="booking-places" aria-label={t("listLabel")}>
            {resources.map((r) => {
              const mapped = view.sensors.find((s) => s.connectionId === connection && s.resourceId === r.id);
              return (
                <li key={r.id} className="booking-row">
                  <FormField label={t("sensorId", { name: pick(r.names, locale) })} name={`sensor-${r.id}`} value={ids[r.id] ?? ""} maxLength={128} autoComplete="off"
                    spellCheck={false} onChange={(e) => setIds((all) => ({ ...all, [r.id]: e.target.value }))} />
                  {mapped?.lastOccupied != null && mapped.lastObservedAt ? (
                    <Badge tone={mapped.lastOccupied ? "info" : "neutral"}>
                      {t(mapped.lastOccupied ? "occupied" : "vacant", { at: format.dateTime(new Date(mapped.lastObservedAt), { timeStyle: "short" }) })}
                    </Badge>
                  ) : mapped ? (
                    <Badge>{t("notRead")}</Badge>
                  ) : null}
                </li>
              );
            })}
          </ul>
          <div className="booking-row">
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
              {t("save")}
            </Button>
          </div>
        </form>
      )}
    </section>
  );
}
