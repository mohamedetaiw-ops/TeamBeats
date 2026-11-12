/*
 * TeamBeats service worker (spec 030 US1, FR-002, FR-019).
 *
 * It keeps the app shell: the offline page, the platform icons and the hashed build files under /_next/static, which
 * hold no personal or tenant data. It never stores an API response or a page, so nothing one person or tenant saw is
 * kept for another; offline reading of critical items is a separate, encrypted store. Pages always come from the
 * network, and the offline page answers when there is none.
 *
 * A new version waits until the person taps "Reload" in the app (SKIP_WAITING), so an update never interrupts a form.
 */
const VERSION = new URL(self.location.href).searchParams.get("v") || "dev";
const SHELL = `tb-shell-${VERSION}`;
const SHELL_FILES = ["/offline", "/icons/icon-192.png", "/icons/icon-512.png"];

self.addEventListener("install", (event) => {
  // Fetched without cookies, so the stored offline page is the signed-out one and carries nothing about this person.
  event.waitUntil(caches.open(SHELL).then((cache) => cache.addAll(SHELL_FILES.map((path) => new Request(path, { credentials: "omit" })))));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) => Promise.all(keys.filter((k) => k.startsWith("tb-shell-") && k !== SHELL).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener("message", (event) => {
  if (event.data && event.data.type === "SKIP_WAITING") self.skipWaiting();
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET") return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;
  // API answers and development routes are never stored (FR-019).
  if (url.pathname.startsWith("/api/") || url.pathname.startsWith("/dev/")) return;

  if (request.mode === "navigate") {
    event.respondWith(fetch(request).catch(() => caches.match("/offline").then((r) => r || Response.error())));
    return;
  }

  // Hashed build files never change, so the first copy serves every later request. A development build reuses file
  // names across edits, so it is never stored there.
  if (VERSION !== "dev" && url.pathname.startsWith("/_next/static/")) {
    event.respondWith(
      caches.open(SHELL).then((cache) =>
        cache.match(request).then(
          (hit) =>
            hit ||
            fetch(request).then((response) => {
              if (response.ok) cache.put(request, response.clone());
              return response;
            }),
        ),
      ),
    );
  }
});
