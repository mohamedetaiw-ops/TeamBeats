import "server-only";
import { cookies, headers } from "next/headers";

/**
 * Server-side calls to the TeamBeats API (backend-for-frontend). The browser never holds a token: the HTTP-only
 * session cookie is forwarded, and the tenant comes from the request host, which the API resolves itself.
 */
const apiOrigin = process.env.TEAMBEATS_API_ORIGIN ?? "http://localhost:5080";

/**
 * The caller's address as the edge proxy saw it: the rightmost x-forwarded-for entry, which the proxy appends.
 * Entries further left come from the client and are ignored, so the API applies network rules (spec 026) to the
 * real caller on server-rendered calls too.
 */
function clientAddress(forwardedFor: string | null): string | null {
  const last = forwardedFor?.split(",").map((part) => part.trim()).filter(Boolean).at(-1);
  return last ?? null;
}

export type ApiResult<T> = { ok: true; data: T } | { ok: false; status: number };

async function apiRequest<T>(path: string, init: { method: string; body?: unknown }): Promise<ApiResult<T>> {
  const cookieStore = await cookies();
  const incoming = await headers();
  const session = cookieStore.get("tb_session")?.value;
  const address = clientAddress(incoming.get("x-forwarded-for"));
  let response: Response;
  try {
    response = await fetch(new URL(path, apiOrigin), {
      method: init.method,
      headers: {
        accept: "application/json",
        "x-forwarded-host": incoming.get("host") ?? "",
        ...(address ? { "x-forwarded-for": address } : {}),
        ...(init.body === undefined ? {} : { "content-type": "application/json" }),
        ...(session ? { cookie: `tb_session=${session}` } : {}),
      },
      body: init.body === undefined ? undefined : JSON.stringify(init.body),
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

export function apiGet<T>(path: string): Promise<ApiResult<T>> {
  return apiRequest<T>(path, { method: "GET" });
}

/** A read-only POST, such as a validation check, made while rendering on the server. */
export function apiPost<T>(path: string, body: unknown): Promise<ApiResult<T>> {
  return apiRequest<T>(path, { method: "POST", body });
}

export type Me = {
  id: string;
  displayName: string;
  locale: string;
  dir: "rtl" | "ltr";
  tenant: { id: string; key: string; enabledLocales: string[]; defaultLocale: string };
};

/** An admin shell section; badge counts items that need attention, such as open security alerts (spec 026). */
export type NavigationSection = { key: string; labelKey: string; path: string; wholeTenant: boolean; badge?: number | null };

export type SignInOptions = { sso: boolean; kind: string | null; displayName: string | null };
