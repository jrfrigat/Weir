// In development, always fetch from the network and do not enable offline support.
// This avoids caching stale assets during iteration. The published variant
// (service-worker.published.js) provides real offline caching.
self.addEventListener('fetch', () => { });
