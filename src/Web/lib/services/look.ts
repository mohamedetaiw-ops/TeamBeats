import type { IconName } from "@/components/ui/Icon";
import type { Category, ServiceCard } from "./types";

const categoryIcons: readonly IconName[] = ["user", "settings", "home", "inbox", "globe", "chat", "calendar", "book"];
const tones = ["accent", "highlight", "success", "info"] as const;

export type ServiceTone = (typeof tones)[number];

/**
 * The icon and tint of a service tile (docs/design-system/components.md, Tile card). Each category keeps one icon and
 * tint, so a service looks the same in the catalogue, in Favourites, in Recently used and on the home.
 */
export function serviceLook(service: ServiceCard, categories: readonly Category[]): { icon: IconName; tone: ServiceTone } {
  const index = Math.max(0, categories.findIndex((c) => c.id === service.categoryId));
  return { icon: categoryIcons[index % categoryIcons.length] ?? "services", tone: tones[index % tones.length] ?? "accent" };
}
