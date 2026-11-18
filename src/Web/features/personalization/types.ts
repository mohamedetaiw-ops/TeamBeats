/** Shapes of the spec 008 personal layout and add panel APIs. The API decides what is allowed; the page sends intents. */
export type LayoutOperation = {
  op: "add" | "hide" | "remove" | "move" | "resize";
  placementId: string;
  componentRef?: string;
  regionId?: string;
  position?: number | null;
  /** A resize's width in grid columns; the API checks it against the component's allowed widths. */
  span?: number;
};

/** `deviceClass` is "any" when the layout applies on every device, or the device class it was kept for (spec 008 FR-032). */
export type LayoutView = { pageId: string; revision: number; eTag: string; operations: LayoutOperation[]; deviceClass?: string };

export type AddPanelItem = {
  componentRef: string;
  category: string;
  eligible: boolean;
  name: string;
  description: string;
  reason: string | null;
};
