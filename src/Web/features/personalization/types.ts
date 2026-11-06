/** Shapes of the spec 008 personal layout and add panel APIs. The API decides what is allowed; the page sends intents. */
export type LayoutOperation = {
  op: "add" | "hide" | "remove" | "move";
  placementId: string;
  componentRef?: string;
  regionId?: string;
  position?: number | null;
};

export type LayoutView = { pageId: string; revision: number; eTag: string; operations: LayoutOperation[] };

export type AddPanelItem = {
  componentRef: string;
  category: string;
  eligible: boolean;
  name: string;
  description: string;
  reason: string | null;
};
