import { iconNames, type IconName } from "@/components/ui/Icon";
import type { Category, ServiceCard } from "./types";

const categoryIcons: readonly IconName[] = ["user", "settings", "home", "inbox", "globe", "chat", "calendar", "book"];
const tones = ["accent", "highlight", "success", "info"] as const;

export type ServiceTone = (typeof tones)[number];

/** Icons a service owner may choose (spec 016 FR-018), in the order the editor lists them. */
export const serviceIcons = [
  "services", "leave", "clock", "calendar", "user", "people", "building", "pin", "files", "folder", "book", "policy",
  "news", "megaphone", "chat", "inbox", "attach", "link", "globe", "lock", "settings", "star", "image", "trend-up",
] as const satisfies readonly IconName[];

const known = new Set<string>(iconNames);

/**
 * The icon and tint of a service tile (docs/design-system/components.md, Tile card). A service shows its own icon
 * (spec 016 FR-018, DLS-081) and its category's tint; without an icon of its own it takes the category's. The same look
 * is used in the catalogue, Favourites, Recently used, the home and the service's page header.
 */
export function serviceLook(service: Pick<ServiceCard, "categoryId" | "icon">, categories: readonly Category[]): { icon: IconName; tone: ServiceTone } {
  const index = Math.max(0, categories.findIndex((c) => c.id === service.categoryId));
  const own = service.icon && known.has(service.icon) ? (service.icon as IconName) : null;
  return { icon: own ?? categoryIcons[index % categoryIcons.length] ?? "services", tone: tones[index % tones.length] ?? "accent" };
}
