import "server-only";
import { cookies, headers } from "next/headers";

/**
 * Server-side calls to the TeamBeats API (backend-for-frontend). The browser never holds a token: the HTTP-only
 * session cookie is forwarded, and the tenant comes from the request host, which the API resolves itself.
 */
const apiOrigin = process.env.TEAMBEATS_API_ORIGIN ?? "http://localhost:5080";

export type ApiResult<T> = { ok: true; data: T } | { ok: false; status: number };

export async function apiGet<T>(path: string): Promise<ApiResult<T>> {
  const cookieStore = await cookies();
  const incoming = await headers();
  const session = cookieStore.get("tb_session")?.value;
  let response: Response;
  try {
    response = await fetch(new URL(path, apiOrigin), {
      headers: {
        accept: "application/json",
        "x-forwarded-host": incoming.get("host") ?? "",
        ...(session ? { cookie: `tb_session=${session}` } : {}),
      },
      cache: "no-store",
    });
  } catch {
    // API unreachable: treat as unavailable so pages degrade to their signed-out state instead of crashing.
    return { ok: false, status: 503 };
  }
  if (!response.ok) {
    return { ok: false, status: response.status };
  }
  return { ok: true, data: (await response.json()) as T };
}

export type Me = {
  id: string;
  displayName: string;
  locale: string;
  dir: "rtl" | "ltr";
  tenant: { id: string; key: string; enabledLocales: string[]; defaultLocale: string };
};

export type NavigationSection = { key: string; labelKey: string; path: string; wholeTenant: boolean };
