// Registers the PWA service worker and bridges its "update available" signal to Blazor, where the
// PwaUpdater component surfaces it as a Flare snackbar toast (Weir's design), like the Flare Gallery.
// Kept as an external file (not an inline <script>) so the app can run under a strict Content-Security-
// Policy that does not allow 'unsafe-inline' scripts. The service-worker plumbing must run here because
// it has to be in place before Blazor starts and around the app.
(function () {
    // ----- Offline banner (independent of the service worker) ------------------------------------
    function toggleOffline() {
        var banner = document.getElementById('weir-offline');
        if (banner) {
            banner.hidden = navigator.onLine;
        }
    }

    window.addEventListener('online', toggleOffline);
    window.addEventListener('offline', toggleOffline);
    toggleOffline();

    // ----- Service-worker update bridge ----------------------------------------------------------
    // State shared between the SW events (which can fire before Blazor is ready) and the Blazor
    // PwaUpdater component (which registers via weirPwa.init once it renders).
    var state = { ready: false, registration: null, dotNet: null, refreshing: false };

    // Public bridge used by the PwaUpdater Blazor component.
    window.weirPwa = {
        // Called by the component after first render; if an update was already found, notify at once.
        init: function (dotNetRef) {
            state.dotNet = dotNetRef;
            if (state.ready) {
                notify();
            }
        },
        // Called from the snackbar's "Update" action: activate the waiting worker; controllerchange
        // (below) then reloads the page once onto the new build.
        apply: function () {
            var registration = state.registration;
            if (registration && registration.waiting) {
                registration.waiting.postMessage('skipWaiting');
            } else {
                window.location.reload();
            }
        }
    };

    function notify() {
        if (state.dotNet) {
            try {
                state.dotNet.invokeMethodAsync('OnUpdateAvailable');
            } catch (e) {
                // The component may have gone away; ignore.
            }
        }
    }

    function markReady(registration) {
        state.ready = true;
        state.registration = registration;
        notify();
    }

    if (!('serviceWorker' in navigator)) {
        return;
    }

    // When the new worker takes control (after skipWaiting), reload once to run the fresh build.
    navigator.serviceWorker.addEventListener('controllerchange', function () {
        if (state.refreshing) {
            return;
        }

        state.refreshing = true;
        window.location.reload();
    });

    navigator.serviceWorker.register('service-worker.js').then(function (registration) {
        // A build may already be installed and waiting from a previous visit.
        if (registration.waiting && navigator.serviceWorker.controller) {
            markReady(registration);
        }

        registration.addEventListener('updatefound', function () {
            var installing = registration.installing;
            if (!installing) {
                return;
            }

            installing.addEventListener('statechange', function () {
                // A new version finished installing while an old one is controlling the page.
                if (installing.state === 'installed' && navigator.serviceWorker.controller) {
                    markReady(registration);
                }
            });
        });
    });
})();
