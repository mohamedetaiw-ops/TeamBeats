"use client";

import type { CSSProperties, MouseEvent } from "react";
import "./plan-map.css";

export type PlanPinState = "free" | "busy" | "unavailable" | "selected";

/** One place on a plan: X and Y are percentages of the image's width and height from its top left corner. */
export type PlanPin = { key: string; x: number; y: number; label: string; state: PlanPinState };

/**
 * Plan map (spec 036 DLS-040, spec 031 R3 US7): an image such as a floor plan with places drawn on it. The image is a
 * picture of a real place, so it never mirrors in right to left languages; only text around it does. Each place is a
 * button named by its label and state, and shape as well as color tells the states apart. The page MUST show the same
 * places and actions as a list next to the map. With <paramref name="onPlace"/>, a click on the image reports where,
 * for editors; the list is the keyboard way to place.
 */
export function PlanMap({
  label,
  src,
  alt,
  width,
  height,
  pins,
  onSelect,
  onPlace,
}: {
  label: string;
  src: string;
  alt: string;
  width: number;
  height: number;
  pins: readonly PlanPin[];
  onSelect?: (key: string) => void;
  onPlace?: (x: number, y: number) => void;
}) {
  function place(e: MouseEvent<HTMLDivElement>) {
    if (!onPlace || e.target !== e.currentTarget.querySelector("img")) return;
    const box = e.currentTarget.getBoundingClientRect();
    const x = Math.round(((e.clientX - box.x) / box.width) * 10000) / 100;
    const y = Math.round(((e.clientY - box.y) / box.height) * 10000) / 100;
    onPlace(Math.min(100, Math.max(0, x)), Math.min(100, Math.max(0, y)));
  }

  return (
    <figure className="ui-planmap" role="group" aria-label={label}>
      {/* eslint-disable-next-line jsx-a11y/click-events-have-key-events, jsx-a11y/no-static-element-interactions -- the list next to the map is the keyboard way to place */}
      <div className="ui-planmap-canvas" style={{ aspectRatio: `${width} / ${height}` } as CSSProperties} data-placing={onPlace ? true : undefined} onClick={place}>
        {/* eslint-disable-next-line @next/next/no-img-element -- a tenant upload served by the API at its own size */}
        <img src={src} alt={alt} width={width} height={height} className="ui-planmap-image" />
        {pins.map((pin) => (
          <button
            key={pin.key}
            type="button"
            className="ui-planmap-pin"
            data-state={pin.state}
            style={{ "--xp-x": `${pin.x}%`, "--xp-y": `${pin.y}%` } as CSSProperties}
            aria-label={pin.label}
            title={pin.label}
            aria-pressed={pin.state === "selected" ? true : undefined}
            onClick={onSelect ? () => onSelect(pin.key) : undefined}
            disabled={!onSelect}
          />
        ))}
      </div>
    </figure>
  );
}
