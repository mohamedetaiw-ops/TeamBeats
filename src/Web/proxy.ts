import { NextResponse, type NextRequest } from "next/server";

/**
 * Content security policy for every page (spec 026 FR-025, spec 032 FR-035): scripts run only with this request's
 * nonce, which Next.js reads from the request header and puts on its own scripts, so no inline script can run.
 * Styles allow inline style attributes, which React sets for layout values; no style can run code. Frames, images,
 * fonts and requests stay on this tenant host. Development adds 'unsafe-eval', which React needs there only.
 */
export function contentSecurityPolicy(nonce: string, dev: boolean): string {
  return [
    "default-src 'self'",
    `script-src 'self' 'nonce-${nonce}' 'strict-dynamic'${dev ? " 'unsafe-eval'" : ""}`,
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' blob: data:",
    "font-src 'self'",
    "connect-src 'self'",
    "frame-src 'self'",
    // The app's service worker (spec 030 FR-002) is a script of this host.
    "worker-src 'self'",
    "manifest-src 'self'",
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
    // The page builder previews a page in a frame on the same host; no other site may frame the intranet.
    "frame-ancestors 'self'",
  ].join("; ");
}

export function proxy(request: NextRequest) {
  const nonce = btoa(crypto.randomUUID());
  const policy = contentSecurityPolicy(nonce, process.env.NODE_ENV === "development");
  const headers = new Headers(request.headers);
  headers.set("x-nonce", nonce);
  headers.set("Content-Security-Policy", policy);
  const response = NextResponse.next({ request: { headers } });
  response.headers.set("Content-Security-Policy", policy);
  return response;
}

export const config = {
  matcher: [
    {
      // API and development routes are answered by the API; static files carry no script of their own.
      source: "/((?!api|dev|oauth|mcp|_next/static|_next/image|favicon.ico).*)",
      missing: [
        { type: "header", key: "next-router-prefetch" },
        { type: "header", key: "purpose", value: "prefetch" },
      ],
    },
  ],
};
