// Load both shipped languages before mounting the console and its localized Flare controls.
// Kept external so startup works with the host's Content-Security-Policy.
Blazor.start({
    configureRuntime: runtime => runtime.withConfig({ loadAllSatelliteResources: true })
});
