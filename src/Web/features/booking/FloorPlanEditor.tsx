"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button, ButtonLink } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { PlanMap, type PlanPin } from "@/components/ui/PlanMap";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import { bookingApi, call, floorMapHref, pick, send, type FloorMapView, type FloorPlanImage } from "@/lib/booking";
import "./booking.css";

type Place = { x: string; y: string };

function placesOf(view: FloorMapView): Record<string, Place> {
  return Object.fromEntries(view.resources.map((r) => [r.id, { x: r.x === null ? "" : String(r.x), y: r.y === null ? "" : String(r.y) }]));
}

/**
 * The floor plan editor for facilities administrators (spec 031 R3 US7, FR-019): upload a PNG, JPEG or WebP image, then
 * place the floor's resources by choosing one and clicking where it is, or by entering its position in the list, which
 * is the keyboard way. The API checks the image and every position.
 */
export function FloorPlanEditor({ initial }: { initial: FloorMapView }) {
  const t = useTranslations("booking.floorEditor");
  const locale = useLocale();
  const [map, setMap] = useState(initial);
  const [places, setPlaces] = useState<Record<string, Place>>(() => placesOf(initial));
  const [selected, setSelected] = useState<string | null>(initial.resources[0]?.id ?? null);
  const [file, setFile] = useState<File | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [done, setDone] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const floor = pick(map.names, locale);

  function shown(view: FloorMapView, message: string) {
    setMessages([]);
    setMap(view);
    setPlaces(placesOf(view));
    setDone(message);
  }

  async function reload(message: string) {
    const result = await call<FloorMapView>(() => fetch(`${bookingApi}/floors/${map.floorId}/map`), t("failed"));
    if (result.ok) shown(result.value, message);
    else setMessages(result.messages);
  }

  async function upload() {
    if (!file) return;
    setBusy("upload");
    setDone(null);
    const result = await call<FloorPlanImage>(
      () => fetch(`${bookingApi}/manage/floors/${map.floorId}/plan`, { method: "PUT", headers: { "Content-Type": file.type || "application/octet-stream" }, body: file }),
      t("failed"),
    );
    setBusy(null);
    if (!result.ok) return setMessages(result.messages);
    setFile(null);
    await reload(t("uploaded"));
  }

  async function remove() {
    setBusy("remove");
    setDone(null);
    const result = await call<void>(() => send(`${bookingApi}/manage/floors/${map.floorId}/plan`, "DELETE"), t("failed"));
    setBusy(null);
    if (!result.ok) return setMessages(result.messages);
    await reload(t("removed"));
  }

  async function save() {
    setBusy("save");
    setDone(null);
    const positions = map.resources.map((r) => {
      const p = places[r.id] ?? { x: "", y: "" };
      return { resourceId: r.id, x: p.x === "" ? null : Number(p.x), y: p.y === "" ? null : Number(p.y) };
    });
    const result = await call<FloorMapView>(() => send(`${bookingApi}/manage/floors/${map.floorId}/positions`, "PUT", { positions }), t("failed"));
    setBusy(null);
    if (!result.ok) return setMessages(result.messages);
    shown(result.value, t("saved"));
  }

  function set(id: string, axis: keyof Place, value: string) {
    setPlaces((current) => ({ ...current, [id]: { ...(current[id] ?? { x: "", y: "" }), [axis]: value } }));
  }

  const pins: PlanPin[] = map.resources.flatMap((r) => {
    const p = places[r.id];
    if (!p || p.x === "" || p.y === "") return [];
    return [{ key: r.id, x: Number(p.x), y: Number(p.y), label: pick(r.names, locale), state: r.id === selected ? ("selected" as const) : ("free" as const) }];
  });

  return (
    <div className="booking">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {done ? <Alert tone="success" title={done} live /> : null}

      <Widget title={t("imageTitle")}>
        <p className="booking-meta">{t("imageIntro")}</p>
        <div className="booking-row">
          <div className="field">
            <label htmlFor="plan-file">{t("file")}</label>
            <input id="plan-file" name="planFile" type="file" accept="image/png,image/jpeg,image/webp" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
          </div>
          <Button variant="primary" icon="upload" disabled={!file} busy={busy === "upload"} busyLabel={t("working")} onClick={() => void upload()}>
            {map.plan ? t("replace") : t("upload")}
          </Button>
          {map.plan ? (
            <Button variant="quiet" icon="trash" busy={busy === "remove"} busyLabel={t("working")} onClick={() => void remove()}>
              {t("remove")}
            </Button>
          ) : null}
          <ButtonLink href={floorMapHref(map.floorId)} variant="quiet">
            {t("view")}
          </ButtonLink>
        </div>
      </Widget>

      {map.plan ? (
        <div className="booking-split">
          <Widget title={t("mapTitle")}>
            <p className="booking-meta">{selected ? t("placeHint", { name: pick(map.resources.find((r) => r.id === selected)?.names, locale) }) : t("chooseHint")}</p>
            <PlanMap label={t("mapLabel", { floor })} src={map.plan.url} alt={t("planAlt", { floor })} width={map.plan.width} height={map.plan.height} pins={pins}
              onSelect={(key) => setSelected(key)}
              onPlace={(x, y) => {
                if (selected) {
                  set(selected, "x", String(x));
                  set(selected, "y", String(y));
                }
              }} />
          </Widget>
          <Widget title={t("placesTitle")}>
            {map.resources.length === 0 ? (
              <StateView kind="empty" title={t("noResourcesTitle")} message={t("noResources")} />
            ) : (
              <form
                className="booking-form"
                aria-label={t("placesTitle")}
                onSubmit={(e) => {
                  e.preventDefault();
                  void save();
                }}
              >
                <ul className="booking-places">
                  {map.resources.map((r) => (
                    <li key={r.id} className="booking-row" data-selected={r.id === selected ? true : undefined}>
                      <Button size="sm" variant={r.id === selected ? "primary" : "quiet"} aria-pressed={r.id === selected} onClick={() => setSelected(r.id)}>
                        {pick(r.names, locale)}
                      </Button>
                      <FormField label={t("x", { name: pick(r.names, locale) })} name={`x-${r.id}`} type="number" min={0} max={100} step={0.01} value={places[r.id]?.x ?? ""}
                        onChange={(e) => set(r.id, "x", e.target.value)} />
                      <FormField label={t("y", { name: pick(r.names, locale) })} name={`y-${r.id}`} type="number" min={0} max={100} step={0.01} value={places[r.id]?.y ?? ""}
                        onChange={(e) => set(r.id, "y", e.target.value)} />
                    </li>
                  ))}
                </ul>
                <div className="booking-row">
                  <Button type="submit" variant="primary" busy={busy === "save"} busyLabel={t("working")}>
                    {t("save")}
                  </Button>
                </div>
              </form>
            )}
          </Widget>
        </div>
      ) : (
        <StateView kind="empty" title={t("noPlanTitle")} message={t("noPlan")} icon="building" />
      )}
    </div>
  );
}
