import "server-only";
import { getLocale, getMessages } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { customRoleName, type RoleView } from "@/lib/admin/types";

/**
 * Role names in the viewer's language: built-in roles from the catalogue, custom roles by the names they were defined
 * with, falling back to their key. Read on the server so client screens get a plain map, also for administrators who
 * may not list roles themselves.
 */
export async function roleNames(roles?: RoleView[]): Promise<Record<string, string>> {
  const messages = (await getMessages()) as { admin?: { roles?: { names?: Record<string, string> } } };
  const builtIn = messages.admin?.roles?.names ?? {};
  const fetched = roles ? null : await apiGet<RoleView[]>("/api/v1/authz/roles");
  const list: RoleView[] = roles ?? (fetched?.ok ? fetched.data : []) ?? [];
  const locale = await getLocale();
  return { ...builtIn, ...Object.fromEntries(list.filter((r) => !builtIn[r.key]).map((r) => [r.key, customRoleName(r, locale) ?? r.key])) };
}
