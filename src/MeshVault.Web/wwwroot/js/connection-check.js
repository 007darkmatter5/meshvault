// Notices when a Blazor circuit never connects, and says so.
//
// Blazor Server sends a fully rendered page first and wires it up afterwards.
// If the wiring never arrives — most often a reverse proxy that will not pass
// WebSockets — the page looks perfect and ignores every click, with nothing on
// screen to explain it. ReconnectModal only handles a circuit that connected
// and then dropped, so this covers the case where one never connected at all.

(function () {
    // How long to wait before concluding it is not coming. A cold container on a
    // spinning array can take a few seconds to answer the first negotiate.
    const grace = 12000;

    window.meshvaultInteractive = function () {
        window.__meshvaultInteractive = true;
        const banner = document.getElementById('offline-ui');
        if (banner) banner.removeAttribute('data-visible');
    };

    setTimeout(function () {
        if (window.__meshvaultInteractive) return;

        // The sign-in pages are statically rendered by design: their form posts
        // work without a circuit, so warning there would be false alarm.
        if (document.querySelector('.auth-card')) return;

        const banner = document.getElementById('offline-ui');
        if (banner) banner.setAttribute('data-visible', '');
    }, grace);
})();
