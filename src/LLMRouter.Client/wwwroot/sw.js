// LLMRouter service worker — SPEC-030.
// Immutable fingerprinted assets (_framework/*, /fonts/*): cache-first.
// Everything else (index.html, css, js, manifest): network-first with cache
// fallback so a new deploy is picked up immediately and the dashboard still
// works offline. GET /api/*: network-first with cache fallback.
// Gateway/streaming paths: network only.
// IMPORTANT: bump CACHE on every release that changes wwwroot — stale entries
// from older versions are wiped on activate.
const CACHE = 'llmrouter-v3';
const STATIC = ['/', '/index.html', '/css/app.css', '/favicon.svg', '/icon-192.png', '/manifest.webmanifest', '/offline.html'];
const IMMUTABLE = /^\/(_framework|fonts)\//;

self.addEventListener('install', e => {
  e.waitUntil(caches.open(CACHE).then(c => c.addAll(STATIC)).then(() => self.skipWaiting()));
});
self.addEventListener('activate', e => {
  e.waitUntil(caches.keys().then(ks => Promise.all(ks.filter(k => k !== CACHE).map(k => caches.delete(k)))).then(() => self.clients.claim()));
});

function networkFirst(request) {
  return fetch(request).then(resp => {
    if (resp.ok) {
      const clone = resp.clone();
      caches.open(CACHE).then(c => c.put(request, clone)).catch(() => {});
    }
    return resp;
  }).catch(() => caches.match(request).then(hit =>
    hit || (request.mode === 'navigate' ? caches.match('/offline.html') : Response.error())));
}

function cacheFirst(request) {
  return caches.match(request).then(hit => hit || fetch(request).then(resp => {
    if (resp.ok) {
      const clone = resp.clone();
      caches.open(CACHE).then(c => c.put(request, clone)).catch(() => {});
    }
    return resp;
  }).catch(() => request.mode === 'navigate' ? caches.match('/offline.html') : Response.error()));
}

self.addEventListener('fetch', e => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET') return;
  // streaming/gateway paths must never hit the cache
  if (url.pathname.startsWith('/v1') || url.pathname.startsWith('/mcp') || url.pathname.startsWith('/a2a')) return;

  e.respondWith(IMMUTABLE.test(url.pathname) ? cacheFirst(e.request) : networkFirst(e.request));
});
