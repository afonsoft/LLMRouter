// LLMRouter service worker — static-asset cache-first, network for API/gateway.
const CACHE = 'llmrouter-v1';
const STATIC = ['/', '/index.html', '/css/app.css', '/favicon.svg', '/icon-192.png', '/manifest.webmanifest', '/offline.html'];

self.addEventListener('install', e => {
  e.waitUntil(caches.open(CACHE).then(c => c.addAll(STATIC)).then(() => self.skipWaiting()));
});
self.addEventListener('activate', e => {
  e.waitUntil(caches.keys().then(ks => Promise.all(ks.filter(k => k !== CACHE).map(k => caches.delete(k)))).then(() => self.clients.claim()));
});
self.addEventListener('fetch', e => {
  const url = new URL(e.request.url);
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/v1') || url.pathname.startsWith('/mcp') || url.pathname.startsWith('/a2a')) return;
  e.respondWith(
    caches.match(e.request).then(hit => hit || fetch(e.request).then(resp => {
      if (resp.ok && e.request.method === 'GET') {
        const clone = resp.clone();
        caches.open(CACHE).then(c => c.put(e.request, clone));
      }
      return resp;
    }).catch(() => e.request.mode === 'navigate' ? caches.match('/offline.html') : Response.error()))
  );
});
