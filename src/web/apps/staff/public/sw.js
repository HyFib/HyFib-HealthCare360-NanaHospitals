/*
 * HC360 staff app service worker (read-only offline mode). It keeps the app's own files (the page and its scripts and
 * styles) so the app still opens when the HC360 server cannot be reached; the offline copy itself is kept encrypted by
 * the app (lib/offline), never here. It never keeps an API answer, a sign-in or anything under /api, /hubs or /health:
 * those always go to the server, and fail while it is down.
 */
const SHELL = 'hc360-shell-v1';

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches
      .open(SHELL)
      .then((cache) => cache.addAll(['/']))
      .then(() => self.skipWaiting()),
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((names) =>
        Promise.all(names.filter((n) => n.startsWith('hc360-shell-') && n !== SHELL).map((n) => caches.delete(n))),
      )
      .then(() => self.clients.claim()),
  );
});

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.method !== 'GET') {
    return;
  }
  const url = new URL(request.url);
  if (url.origin !== self.location.origin || /^\/(api|hubs|health|portal|signin|connect)(\/|$)/.test(url.pathname)) {
    return;
  }

  // The page: from the server when it answers (so a new version is picked up), otherwise the kept one.
  if (request.mode === 'navigate') {
    event.respondWith(
      fetch(request)
        .then((response) => {
          if (response.ok) {
            const copy = response.clone();
            void caches.open(SHELL).then((cache) => cache.put('/', copy));
          }
          return response;
        })
        .catch(() => caches.match('/').then((kept) => kept ?? Response.error())),
    );
    return;
  }

  // Built files carry their content's hash in their name: once kept, they never change.
  if (url.pathname.startsWith('/assets/')) {
    event.respondWith(
      caches.match(request).then(
        (kept) =>
          kept ??
          fetch(request).then((response) => {
            if (response.ok) {
              const copy = response.clone();
              void caches.open(SHELL).then((cache) => cache.put(request, copy));
            }
            return response;
          }),
      ),
    );
  }
});
