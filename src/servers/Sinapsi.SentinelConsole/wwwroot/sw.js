/*
 * Minimal service worker for the Sinapsi Sentinel PWA (v1).
 *
 * Scope: installability + an offline app shell only — deliberately tiny, matching the
 * Sinapsi Console/Studio approach (Sinapsi/sinapsi-app · apps/console/public/sw.js). It
 * caches the navigation shell + branding assets so a cold, offline launch still boots the
 * Sentinel UI (which then shows its honest "approval bus down / reconnecting" state until
 * the bus + broker are reachable). It NEVER caches /api/* or the /events SSE stream — the
 * live decision feed, plane posture and pending-approval queue must always be fresh
 * (freshness is decision-critical for an approval surface). Those requests bypass the SW.
 */
const CACHE = "sinapsi-sentinel-shell-v1";
const SHELL = [
  "/",
  "/index.html",
  "/manifest.webmanifest",
  "/favicon.svg",
  "/favicon.ico",
  "/sentinel-mark.svg",
  "/apple-touch-icon.png",
  "/icon-192.png",
  "/icon-512.png",
  "/maskable-512.png",
];

self.addEventListener("install", (event) => {
  event.waitUntil(
    caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()),
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener("fetch", (event) => {
  const req = event.request;
  const url = new URL(req.url);

  // Never intercept live data / cross-origin / non-GET — must not be served stale.
  if (req.method !== "GET" || url.origin !== self.location.origin) return;
  if (url.pathname.startsWith("/api") || url.pathname === "/events") return;
  if (req.headers.get("accept")?.includes("text/event-stream")) return;

  // App-shell: network-first for navigations, falling back to the cached shell offline.
  if (req.mode === "navigate") {
    event.respondWith(
      fetch(req)
        .then((res) => {
          const copy = res.clone();
          caches.open(CACHE).then((c) => c.put("/index.html", copy));
          return res;
        })
        .catch(() => caches.match("/index.html").then((r) => r || caches.match("/"))),
    );
    return;
  }

  // Static assets (icons / manifest / svg): cache-first, fall back to network.
  event.respondWith(caches.match(req).then((cached) => cached || fetch(req)));
});
