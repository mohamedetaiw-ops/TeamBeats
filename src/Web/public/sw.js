/*
 * TeamBeats service worker (spec 030 US1, FR-002, FR-019).
 *
 * It keeps the app shell: the offline page, the platform icons and the hashed build files under /_next/static, which
 * hold no personal or tenant data, and the offline reader script. It never stores an API response or a page, so nothing one person or tenant saw is
 * kept for another; offline reading of critical items is a separate, encrypted store. Pages always come from the
 * network, and the offline page answers when there is none.
 *
 * A new version waits until the person taps "Reload" in the app (SKIP_WAITING), so an update never interrupts a form.
 *
 * Push messages (spec 030 US2) carry only a category title, a short generic text and a link inside this app, never item
 * content (FR-007). Tapping one focuses an open window of the app on that link, or opens one.
 */
const VERSION = new URL(self.location.href).searchParams.get("v") || "dev";
const SHELL = `tb-shell-${VERSION}`;
const SHELL_FILES = ["/offline", "/offline-reader.js", "/icons/icon-192.png", "/icons/icon-512.png"];

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

  // The offline reader and the icons come from the shell cache first, so the offline page works with no connection.
  if (SHELL_FILES.includes(url.pathname) && url.pathname !== "/offline") {
    event.respondWith(caches.match(url.pathname).then((hit) => hit || fetch(request)));
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

self.addEventListener("push", (event) => {
  let message = {};
  try {
    message = event.data ? event.data.json() : {};
  } catch {
    message = {};
  }
  const title = typeof message.title === "string" && message.title ? message.title : "TeamBeats";
  const options = {
    body: typeof message.body === "string" ? message.body : "",
    icon: "/icons/icon-192.png",
    badge: "/icons/icon-192.png",
    tag: typeof message.tag === "string" ? message.tag : undefined,
    data: { link: sameOriginPath(message.link) },
  };
  event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const target = new URL((event.notification.data && event.notification.data.link) || "/notifications", self.location.origin).href;
  event.waitUntil(
    self.clients.matchAll({ type: "window", includeUncontrolled: true }).then((windows) => {
      const open = windows.find((w) => new URL(w.url).origin === self.location.origin);
      if (open) return open.focus().then((w) => (w && "navigate" in w ? w.navigate(target) : undefined));
      return self.clients.openWindow(target);
    }),
  );
});

/** Only a path inside this app is followed, so a message can never open another site. */
function sameOriginPath(link) {
  if (typeof link !== "string" || !link.startsWith("/") || link.startsWith("//")) return "/notifications";
  try {
    const url = new URL(link, self.location.origin);
    return url.origin === self.location.origin ? url.pathname + url.search + url.hash : "/notifications";
  } catch {
    return "/notifications";
  }
}
