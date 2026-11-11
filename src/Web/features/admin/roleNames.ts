import "server-only";
import { getMessages } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { RoleView } from "@/lib/admin/types";

/**
 * Role names in the viewer's language: built-in roles from the catalogue, custom roles by their key. Read on the
 * server so client screens get a plain map, also for administrators who may not list roles themselves.
 */
export async function roleNames(roles?: RoleView[]): Promise<Record<string, string>> {
  const messages = (await getMessages()) as { admin?: { roles?: { names?: Record<string, string> } } };
  const builtIn = messages.admin?.roles?.names ?? {};
  const fetched = roles ? null : await apiGet<RoleView[]>("/api/v1/authz/roles");
  const list: RoleView[] = roles ?? (fetched?.ok ? fetched.data : []) ?? [];
  return { ...builtIn, ...Object.fromEntries(list.filter((r) => !builtIn[r.key]).map((r) => [r.key, r.key])) };
}
