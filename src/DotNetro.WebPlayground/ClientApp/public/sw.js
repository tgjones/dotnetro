// Cache-first for the fingerprinted .NET payload (_framework/*.<hash>.dll|wasm|js, optionally .gz):
// file names change with their content, so a cached entry is never stale. The unfingerprinted
// entry points (dotnet.js, dotnet.boot.js, which names the current fingerprinted files) must never
// be cached here, or a new publish would keep loading the old build; they are revalidated instead.
// Bump the cache name to evict entries written by an older version of this worker.
const CACHE = 'dotnetro-framework-v2';
const FINGERPRINTED = /\.[a-z0-9]{10}\.(dll|wasm|js)(\.gz)?$/;

self.addEventListener('install', () => self.skipWaiting());

self.addEventListener('activate', event => {
  event.waitUntil((async () => {
    for (const key of await caches.keys()) if (key !== CACHE) await caches.delete(key);
    await self.clients.claim();
  })());
});

self.addEventListener('fetch', event => {
  const { request } = event;
  if (request.method !== 'GET' || !new URL(request.url).pathname.includes('/_framework/')) return;
  if (!FINGERPRINTED.test(new URL(request.url).pathname)) {
    event.respondWith(fetch(request, { cache: 'no-cache' }));
    return;
  }
  event.respondWith((async () => {
    const cache = await caches.open(CACHE);
    const hit = await cache.match(request);
    if (hit) return hit;
    const response = await fetch(request);
    if (response.ok) cache.put(request, response.clone());
    return response;
  })());
});
