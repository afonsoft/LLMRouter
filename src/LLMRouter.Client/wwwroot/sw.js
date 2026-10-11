// LLMRouter service worker — SPEC-030.
// Immutable fingerprinted assets (_framework/*, /fonts/*): cache-first.
// Everything else (index.html, css, js, manifest, /api/*): network-first with
// cache fallback so a new deploy is picked up immediately and the dashboard
// still works offline. Gateway/streaming paths: network only.
// IMPORTANT: bump CACHE on every release that changes wwwroot — stale entries
// from older versions are wiped on activate.
const CACHE = 'llmrouter-v3';
const STATIC = ['/', '/index.html', '/css/app.css', '/favicon.svg', '/icon-192.png', '/manifest.webmanifest', '/offline.html'];
const IMMUTABLE = /^\/(_framework|fonts)\//;

self.addEventListener('install', e => {
  e.waitUntil((async () => {
    const c = await caches.open(CACHE);
    await c.addAll(STATIC);
    await self.skipWaiting();
  })());
});
self.addEventListener('activate', e => {
  e.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k)));
    await self.clients.claim();
  })());
});

async function putInCache(request, response) {
  try {
    const c = await caches.open(CACHE);
    await c.put(request, response);
  } catch { /* quota/blocked — non-fatal */ }
}

async function networkFirst(request) {
  try {
    const resp = await fetch(request);
    if (resp.ok) await putInCache(request, resp.clone());
    return resp;
  } catch {
    const hit = await caches.match(request);
    return hit || (request.mode === 'navigate' ? caches.match('/offline.html') : Response.error());
  }
}

async function cacheFirst(request) {
  const hit = await caches.match(request);
  if (hit) return hit;
  try {
    const resp = await fetch(request);
    if (resp.ok) await putInCache(request, resp.clone());
    return resp;
  } catch {
    return request.mode === 'navigate' ? caches.match('/offline.html') : Response.error();
  }
}

self.addEventListener('fetch', e => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET') return;
  // streaming/gateway paths must never hit the cache
  if (url.pathname.startsWith('/v1') || url.pathname.startsWith('/mcp') || url.pathname.startsWith('/a2a')) return;

  e.respondWith(IMMUTABLE.test(url.pathname) ? cacheFirst(e.request) : networkFirst(e.request));
});
