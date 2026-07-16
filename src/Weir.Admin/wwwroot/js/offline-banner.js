// Toggles the offline banner (#weir-offline) as connectivity changes. This is independent of the
// service worker: Flare's IVersionCheckService (service-worker mode) now registers the worker and drives
// "new version available" updates, so this file only owns the offline indicator. Kept as an external
// file (not an inline <script>) so the app runs under a strict Content-Security-Policy that does not
// allow 'unsafe-inline' scripts.
(function () {
    function toggleOffline() {
        var banner = document.getElementById('weir-offline');
        if (banner) {
            banner.hidden = navigator.onLine;
        }
    }

    window.addEventListener('online', toggleOffline);
    window.addEventListener('offline', toggleOffline);
    toggleOffline();
})();
