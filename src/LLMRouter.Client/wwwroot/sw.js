// LLMRouter service worker — SPEC-030.
// Static assets: cache-first. GET /api/*: network-first with cache fallback
// (dashboard reads stay usable offline). Gateway/streaming paths: network only.
const CACHE = 'llmrouter-v2';
const STATIC = ['/', '/index.html', '/css/app.css', '/favicon.svg', '/icon-192.png', '/manifest.webmanifest', '/offline.html'];

self.addEventListener('install', e => {
  e.waitUntil(caches.open(CACHE).then(c => c.addAll(STATIC)).then(() => self.skipWaiting()));
});
self.addEventListener('activate', e => {
  e.waitUntil(caches.keys().then(ks => Promise.all(ks.filter(k => k !== CACHE).map(k => caches.delete(k)))).then(() => self.clients.claim()));
});
self.addEventListener('fetch', e => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET') return;
  // streaming/gateway paths must never hit the cache
  if (url.pathname.startsWith('/v1') || url.pathname.startsWith('/mcp') || url.pathname.startsWith('/a2a')) return;

  if (url.pathname.startsWith('/api/')) {
    // network-first, fall back to last cached response
    e.respondWith(
      fetch(e.request).then(resp => {
        if (resp.ok) {
          const clone = resp.clone();
          caches.open(CACHE).then(c => c.put(e.request, clone)).catch(() => {});
        }
        return resp;
      }).catch(() => caches.match(e.request).then(hit => hit || Response.error()))
    );
    return;
  }

  e.respondWith(
    caches.match(e.request).then(hit => hit || fetch(e.request).then(resp => {
      if (resp.ok) {
        const clone = resp.clone();
        caches.open(CACHE).then(c => c.put(e.request, clone)).catch(() => {});
      }
      return resp;
    }).catch(() => e.request.mode === 'navigate' ? caches.match('/offline.html') : Response.error()))
  );
});
