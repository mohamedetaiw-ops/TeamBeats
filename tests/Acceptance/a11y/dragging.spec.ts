import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";

/**
 * Dragging movements (spec 038 A11Y-019, WCAG 2.5.7 and 2.1.1): every part of the web app that moves things by
 * dragging is listed here with the single pointer and keyboard alternative it offers and the feature test that checks
 * it. A new drag fails this test until its alternative is built and listed.
 */
const web = fileURLToPath(new URL("../../../src/Web", import.meta.url));

const alternatives: Record<string, string> = {
  "features/builder/editor/PageEditor.tsx": "arrow keys and the Move to region buttons, announced (specs/page-builder.spec.ts)",
  "features/personalization/Tools.tsx": "Move up, Move down and move to region buttons, announced (specs/home-personalization.spec.ts)",
  "features/spaces/Board.tsx": "the task's Move to menu (specs/spaces.spec.ts)",
  "features/processes/Modeler.tsx": "arrow keys move the focused shape and the Connect to field links steps (specs/process-operations.spec.ts)",
  "features/services/RouteDesigner.tsx": "the palette adds after the selected step and each step's path field links it (specs/route-designer.spec.ts)",
  "components/ui/Patterns.tsx": "the board card's Move menu in the patterns gallery (specs/components.spec.ts)",
};

const drags = /draggable(=\{(?!false)|\s|>)|onDragStart|useDragHandle\(|<ReactFlow\b/;

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((entry) => {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) return entry === "node_modules" || entry.startsWith(".") ? [] : sources(full);
    return /\.tsx$/.test(entry) ? [full] : [];
  });
}

test("every drag in the web app has a listed non-drag alternative", () => {
  const found = ["app", "components", "features"]
    .flatMap((dir) => sources(join(web, dir)))
    .filter((file) => drags.test(readFileSync(file, "utf8")))
    .map((file) => relative(web, file).replace(/\\/g, "/"));
  expect(found.filter((file) => !(file in alternatives)), "drags without a listed alternative").toEqual([]);
  expect(Object.keys(alternatives).filter((file) => !found.includes(file)), "listed drags that no longer exist").toEqual([]);
});
