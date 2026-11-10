import type { DraftPlacement, PageDefinition, PropertySchema } from "@/lib/experience/builder-types";

export type { PickerItem, PropertySchema } from "@/lib/experience/builder-types";

/**
 * Pure edits on a page draft. Every builder operation is one of these, so undo and redo replay whole drafts and the
 * keyboard, the action menu and pointer use all share the same code (spec 032 FR-040, FR-041).
 */
function locate(draft: PageDefinition, id: string): { region: number; index: number } | null {
  for (let region = 0; region < draft.regions.length; region++) {
    const index = draft.regions[region]!.placements.findIndex((p) => p.id === id);
    if (index >= 0) return { region, index };
  }
  return null;
}

function withRegions(draft: PageDefinition, change: (regions: DraftPlacement[][]) => void): PageDefinition {
  const regions = draft.regions.map((r) => [...r.placements]);
  change(regions);
  return { ...draft, regions: draft.regions.map((r, i) => ({ ...r, placements: regions[i]! })) };
}

/** A placement id the validator accepts and the page does not use yet: lowercase letters, digits and hyphens. */
export function newPlacementId(draft: PageDefinition, componentRef: string): string {
  const used = new Set(draft.regions.flatMap((r) => r.placements.map((p) => p.id)));
  const base = (componentRef.split("@")[0]!.split(".").pop() ?? "item").replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 30) || "item";
  for (let n = 1; ; n++) {
    const id = `${base}-${n}`;
    if (!used.has(id)) return id;
  }
}

/** Defaults from the schema, so a new placement starts valid wherever the manifest says how. */
export function defaults(schema: Record<string, PropertySchema> | null): Record<string, unknown> {
  const props: Record<string, unknown> = {};
  for (const [name, s] of Object.entries(schema ?? {})) {
    if (s.default !== undefined && s.default !== null) props[name] = s.default;
  }
  return props;
}

export function add(draft: PageDefinition, regionId: string, placement: DraftPlacement): PageDefinition {
  return withRegions(draft, (regions) => {
    const r = draft.regions.findIndex((x) => x.id === regionId);
    if (r >= 0) regions[r]!.push(placement);
  });
}

export function remove(draft: PageDefinition, id: string): PageDefinition {
  const at = locate(draft, id);
  if (!at) return draft;
  return withRegions(draft, (regions) => void regions[at.region]!.splice(at.index, 1));
}

/** Moves one step earlier or later inside its region. Returns the draft unchanged at either end. */
export function move(draft: PageDefinition, id: string, step: -1 | 1): PageDefinition {
  const at = locate(draft, id);
  if (!at) return draft;
  const target = at.index + step;
  if (target < 0 || target >= draft.regions[at.region]!.placements.length) return draft;
  return withRegions(draft, (regions) => {
    const list = regions[at.region]!;
    [list[at.index], list[target]] = [list[target]!, list[at.index]!];
  });
}

/** Moves to the end of another region. */
export function moveTo(draft: PageDefinition, id: string, regionId: string): PageDefinition {
  const at = locate(draft, id);
  const target = draft.regions.findIndex((r) => r.id === regionId);
  if (!at || target < 0 || target === at.region) return draft;
  return withRegions(draft, (regions) => {
    const [placement] = regions[at.region]!.splice(at.index, 1);
    regions[target]!.push(placement!);
  });
}

/** Puts a placement at a position in a region, counted without the placement itself (drag and drop). */
export function place(draft: PageDefinition, id: string, regionId: string, position: number): PageDefinition {
  const at = locate(draft, id);
  const target = draft.regions.findIndex((r) => r.id === regionId);
  if (!at || target < 0 || (target === at.region && position === at.index)) return draft;
  return withRegions(draft, (regions) => {
    const [placement] = regions[at.region]!.splice(at.index, 1);
    const list = regions[target]!;
    list.splice(Math.max(0, Math.min(position, list.length)), 0, placement!);
  });
}

/** Copies a placement right after itself with a new id; returns the new id too. */
export function duplicate(draft: PageDefinition, id: string): { draft: PageDefinition; id: string | null } {
  const at = locate(draft, id);
  if (!at) return { draft, id: null };
  const source = draft.regions[at.region]!.placements[at.index]!;
  const copyId = newPlacementId(draft, source.componentRef);
  const copy: DraftPlacement = { ...source, id: copyId, props: source.props ? structuredClone(source.props) : source.props };
  return { draft: withRegions(draft, (regions) => void regions[at.region]!.splice(at.index + 1, 0, copy)), id: copyId };
}

export function setProps(draft: PageDefinition, id: string, props: Record<string, unknown>): PageDefinition {
  const at = locate(draft, id);
  if (!at) return draft;
  return withRegions(draft, (regions) => {
    regions[at.region]![at.index] = { ...regions[at.region]![at.index]!, props };
  });
}

export function regionOf(draft: PageDefinition, id: string): string | null {
  const at = locate(draft, id);
  return at ? draft.regions[at.region]!.id : null;
}

/** Undo and redo over whole drafts (FR-041). A new edit drops the redo branch. */
export type History = { past: PageDefinition[]; present: PageDefinition; future: PageDefinition[] };

const LIMIT = 100;

export function record(history: History, next: PageDefinition): History {
  if (next === history.present) return history;
  return { past: [...history.past, history.present].slice(-LIMIT), present: next, future: [] };
}

export function undo(history: History): History {
  const previous = history.past.at(-1);
  if (!previous) return history;
  return { past: history.past.slice(0, -1), present: previous, future: [history.present, ...history.future] };
}

export function redo(history: History): History {
  const next = history.future[0];
  if (!next) return history;
  return { past: [...history.past, history.present], present: next, future: history.future.slice(1) };
}
