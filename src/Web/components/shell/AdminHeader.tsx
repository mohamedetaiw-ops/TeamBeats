import type { ReactNode } from "react";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/ui/PageHeader";
import type { IconName } from "@/components/ui/Icon";

/** One icon per admin section (DLS-081), shown in the page header tile and the overview shortcuts. */
export const adminSectionIcons: Record<string, IconName> = {
  tenant_settings: "settings",
  organization: "building",
  users: "people",
  roles: "check",
  plan: "star",
  identity: "globe",
  audit: "book",
  connections: "link",
  hr_integration: "download",
  pages: "files",
  templates: "folder",
  branding: "image",
  shell: "menu",
  notifications: "bell",
  calendars: "calendar",
  leave: "leave",
  journeys: "pin",
  attendance: "clock",
  letters: "inbox",
  data_changes: "edit",
  expenses: "attach",
  people: "user",
  services: "services",
  processes: "arrow-forward",
  decisions: "filter",
  process_operations: "sort",
  security: "lock",
  ai: "spark",
  search: "search",
  agents: "chat",
  evaluation: "eye",
  analytics: "trend-up",
  kiosks: "home",
  api: "external",
};

/**
 * The page header of an admin section (spec 006 FR-020, docs/design-system/templates.md Admin pages): the section's
 * icon tile, its menu name as the one h1, a one line description, the main action and the section tabs. A tab or record
 * page inside the section passes its own `title`; the section page itself never does, so its title is always the menu
 * name the administrator clicked.
 */
export async function AdminHeader({
  section,
  title,
  description,
  actions,
  nav,
}: {
  section: string;
  title?: string;
  description?: ReactNode;
  actions?: ReactNode;
  nav?: ReactNode;
}) {
  const t = await getTranslations("admin");
  return (
    <PageHeader
      title={title ?? t(`nav.${section}`)}
      description={description}
      icon={adminSectionIcons[section] ?? "settings"}
      actions={actions}
      nav={nav}
    />
  );
}
