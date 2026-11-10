"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Icon } from "@/components/ui/Icon";
import { useDragHandle, type DragTargets } from "@/lib/experience/drag";
import type { PersonalRight } from "@/lib/experience/types";
import { usePersonalization } from "./Personalization";
import type { AddPanelItem } from "./types";

type Target = { id: string; rights: PersonalRight[] };

const targetsOnPage: DragTargets = { item: ".xp-placement[data-placement-id]", itemKey: "placementId", region: ".xp-region[data-region]", regionKey: "region" };

/**
 * Actions on one placement in personalization mode (spec 008 FR-024, FR-025): drag the handle to move an item within
 * its area or to another one, or use move up, move down and move to another area from the keyboard; hide an item of
 * the page, remove one of the user's own. Only actions the API granted for this region appear; the API checks them
 * again on save.
 */
export function PlacementTools({
  placementId,
  regionId,
  index,
  count,
  personal,
  rights,
  targets,
  neighbor,
}: {
  placementId: string;
  regionId: string;
  index: number;
  count: number;
  personal: boolean;
  rights: PersonalRight[];
  targets: Target[];
  /** The item that takes the focus when this one leaves the page. */
  neighbor: string | null;
}) {
  const t = useTranslations("personalization");
  const p = usePersonalization();
  const group = useRef<HTMLDivElement>(null);
  const focus = p?.focus;
  const clearFocus = p?.clearFocus;

  useEffect(() => {
    // After a change the page re-renders from the API; focus follows the item to where it now is.
    if (focus && focus.placementId === placementId && focus.regionId === regionId && focus.index === index) {
      group.current?.querySelector<HTMLButtonElement>("button:not([disabled]):not(.pz-handle)")?.focus();
      clearFocus?.();
    }
  }, [focus, clearFocus, placementId, regionId, index]);

  const reorder = rights.includes("reorder");
  const others = reorder ? targets.filter((r) => r.id !== regionId && r.rights.includes("reorder") && r.rights.includes("add")) : [];

  function move(to: string, position: number | null, text: string) {
    void p!.change({ op: "move", placementId, regionId: to, position }, { placementId, regionId: to, index: position ?? 0 }, text);
  }

  // Drag and drop with the handle, within this area or into another one that takes items; the move buttons stay for
  // the keyboard.
  const drag = useDragHandle({
    id: placementId,
    allowed: new Set([regionId, ...others.map((r) => r.id)]),
    targets: targetsOnPage,
    disabled: !p || p.busy,
    onDrop: (drop) => {
      const area = p!.regionNumber(regionId);
      if (drop.regionId !== regionId) move(drop.regionId, drop.position, t("movedTo", { region: p!.regionNumber(drop.regionId) }));
      else if (drop.position !== index) move(regionId, drop.position, t("moved", { position: drop.position + 1, count, region: area }));
    },
  });

  if (!p?.editing || rights.length === 0) return null;
  const region = p.regionNumber(regionId);
  const label = t("item", { position: index + 1, count, region });
  const canDrop = personal || rights.includes("hide");

  return (
    <div ref={group} className="pz-tools" role="group" aria-label={label}>
      <span className="pz-tools-label" aria-hidden="true">
        {label}
      </span>
      {reorder ? (
        <>
          <button
            type="button"
            className="button button-quiet pz-handle"
            // A pointer control: keyboard users move with the buttons next to it, so it takes no tab stop.
            tabIndex={-1}
            disabled={p.busy}
            title={t("drag")}
            aria-label={t("drag")}
            {...drag}
          >
            <Icon name="menu" size={18} />
          </button>
          <Button variant="quiet" disabled={index === 0 || p.busy} onClick={() => move(regionId, index - 1, t("moved", { position: index, count, region }))}>
            {t("moveUp")}
          </Button>
          <Button variant="quiet" disabled={index === count - 1 || p.busy} onClick={() => move(regionId, index + 1, t("moved", { position: index + 2, count, region }))}>
            {t("moveDown")}
          </Button>
        </>
      ) : null}
      {others.map((r) => (
        <Button key={r.id} variant="quiet" disabled={p.busy} onClick={() => move(r.id, 0, t("movedTo", { region: p.regionNumber(r.id) }))}>
          {t("moveTo", { region: p.regionNumber(r.id) })}
        </Button>
      ))}
      {canDrop ? (
        <Button
          variant="quiet"
          disabled={p.busy}
          onClick={() =>
            void p.change(
              { op: personal ? "remove" : "hide", placementId },
              neighbor ? { placementId: neighbor, regionId, index: index === count - 1 ? index - 1 : index } : undefined,
              personal ? t("removed") : t("hidden"),
            )
          }
        >
          {personal ? t("remove") : t("hide")}
        </Button>
      ) : null}
    </div>
  );
}

/** The add panel of one region (spec 008 FR-023): components the API says the user may add here, and why others cannot. */
export function RegionTools({ regionId, rights }: { regionId: string; rights: PersonalRight[] }) {
  const t = useTranslations("personalization");
  const p = usePersonalization();
  const [items, setItems] = useState<AddPanelItem[] | null>(null);
  const [open, setOpen] = useState(false);
  const panelId = useId();
  const trigger = useRef<HTMLButtonElement>(null);

  if (!p?.editing || !rights.includes("add")) return null;
  const region = p.regionNumber(regionId);

  async function show() {
    setOpen(true);
    setItems(null);
    const response = await fetch(`/api/v1/me/pages/${p!.pageId}/add-panel?region=${encodeURIComponent(regionId)}`);
    setItems(response.ok ? ((await response.json()) as AddPanelItem[]) : []);
  }

  function close() {
    setOpen(false);
    requestAnimationFrame(() => trigger.current?.focus());
  }

  async function add(item: AddPanelItem) {
    const placementId = `my-${crypto.randomUUID().slice(0, 12)}`;
    const ok = await p!.change(
      { op: "add", placementId, componentRef: item.componentRef, regionId, position: 0 },
      { placementId, regionId, index: 0 },
      t("added", { name: item.name, region }),
    );
    if (ok) setOpen(false);
  }

  return (
    <div className="pz-region-tools" onKeyDown={(e) => e.key === "Escape" && open && close()}>
      <Button ref={trigger} variant="secondary" aria-expanded={open} aria-controls={panelId} onClick={() => (open ? close() : void show())}>
        {t("add", { region })}
      </Button>
      {open ? (
        <section id={panelId} className="pz-add-panel" aria-label={t("addPanel", { region })} aria-busy={items === null || undefined}>
          {items === null ? (
            <p>{t("loading")}</p>
          ) : (
            <ul>
              {items.map((item) => (
                <li key={item.componentRef} className="pz-add-item">
                  <span className="pz-add-name">
                    <bdi>{item.name}</bdi>
                  </span>
                  <span className="pz-add-description">{item.eligible ? item.description : item.reason}</span>
                  {item.eligible ? (
                    <Button variant="secondary" disabled={p.busy} onClick={() => void add(item)} aria-label={t("addItem", { name: item.name })}>
                      {t("addShort")}
                    </Button>
                  ) : null}
                </li>
              ))}
            </ul>
          )}
          <Button variant="quiet" onClick={close}>
            {t("close")}
          </Button>
        </section>
      ) : null}
    </div>
  );
}
