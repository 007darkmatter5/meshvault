// Browser-side half of the diagnostics page. Deliberately plain JavaScript with
// no Blazor involvement: the faults worth diagnosing here are exactly the ones
// where the circuit never connects, and a check that needs a working circuit to
// report a broken circuit is no check at all.

(function () {
    // This script is in the page body, so it runs before blazor.web.js at the
    // end of it. Anything that asks "did X load" has to wait and re-ask rather
    // than answer from where it sits in the document.
    function waitFor(test, onSettled) {
        let waited = 0;
        const timer = setInterval(function () {
            waited += 250;
            const answer = test();
            if (answer) {
                clearInterval(timer);
                onSettled(true);
            } else if (waited >= 15000) {
                clearInterval(timer);
                onSettled(false);
            }
        }, 250);
    }

    function row(label, ok, detail) {
        const dt = document.createElement('dt');
        dt.textContent = label;
        const dd = document.createElement('dd');
        dd.className = ok === null ? 'diag-pending' : ok ? 'diag-ok' : 'diag-bad';
        dd.textContent = (ok === null ? '… ' : ok ? '✓ ' : '✗ ') + detail;
        const list = document.getElementById('diag-browser');
        if (!list) return null;
        list.appendChild(dt);
        list.appendChild(dd);
        return dd;
    }

    function set(cell, ok, detail) {
        if (!cell) return;
        cell.className = ok ? 'diag-ok' : 'diag-bad';
        cell.textContent = (ok ? '✓ ' : '✗ ') + detail;
    }

    // The placeholder only stands if this file never ran.
    const list = document.getElementById('diag-browser');
    if (list) list.replaceChildren();

    // Blazor's own script. Without it nothing on any page responds.
    const blazorCell = row('Blazor script', null, 'waiting…');
    waitFor(function () { return typeof window.Blazor !== 'undefined'; }, function (loaded) {
        set(blazorCell, loaded,
            loaded ? 'loaded' : 'never appeared — blazor.web.js did not run');
    });

    // Read from the stylesheet list rather than from a computed style, so the
    // answer does not depend on guessing which MudBlazor class carries which
    // rule.
    const styleCell = row('MudBlazor stylesheet', null, 'waiting…');
    waitFor(function () {
        return Array.prototype.some.call(document.styleSheets, function (sheet) {
            return (sheet.href || '').indexOf('MudBlazor') !== -1;
        });
    }, function (present) {
        set(styleCell, present,
            present ? 'loaded' : 'missing — dialogs and menus will be unstyled or dead');
    });

    // The interactive island in the layout sets this once its circuit is live.
    // This is the check that matters: everything else can be perfect and the app
    // still ignore every click.
    const interactiveCell = row('Interactive rendering', null, 'waiting for the circuit…');
    waitFor(function () { return window.__meshvaultInteractive === true; }, function (connected) {
        set(interactiveCell, connected,
            connected
                ? 'connected — buttons and dialogs will respond'
                : 'never connected — the page renders but nothing on it will respond');
    });

    // Cloudflare, which is the one proxy that breaks this app by helping.
    // Rocket Loader defers and reorders every script on the page to improve a
    // page-speed score; applied to blazor.web.js it stops the circuit ever
    // starting, and the symptom is a perfect-looking page that ignores clicks.
    const injected = Array.prototype.map.call(
        document.querySelectorAll('script[src*="/cdn-cgi/"]'), function (s) { return s.src; });
    const rocketLoader = injected.some(function (src) { return src.indexOf('rocket-loader') !== -1; })
        || document.querySelector('script[type="text/rocketscript"], script[data-cf-settings]') !== null;

    if (rocketLoader) {
        row('Cloudflare', false,
            'Rocket Loader is rewriting scripts on this page — it stops Blazor starting. '
            + 'Turn it off for this hostname (Speed → Optimization, or a page rule).');
    } else if (injected.length > 0) {
        row('Cloudflare', true,
            'injecting ' + injected.length + ' script(s), but not Rocket Loader');
    } else {
        row('Cloudflare', true, 'not rewriting this page');
    }

    // Asset delivery, which a reverse proxy can break on its own.
    [
        ['Blazor framework asset', '_framework/blazor.web.js'],
        ['MudBlazor script asset', '_content/MudBlazor/MudBlazor.min.js'],
        ['MudBlazor style asset', '_content/MudBlazor/MudBlazor.min.css']
    ].forEach(function (pair) {
        const cell = row(pair[0], null, 'checking…');
        fetch(pair[1], { method: 'GET', cache: 'no-store' })
            .then(function (response) {
                return response.text().then(function (body) {
                    // A proxy or a wrong content root can answer 200 with an
                    // empty body, which looks fine in a network tab summary.
                    const ok = response.ok && body.length > 0;
                    set(cell, ok, 'HTTP ' + response.status + ', ' + body.length.toLocaleString() + ' bytes');
                });
            })
            .catch(function (error) { set(cell, false, String(error)); });
    });

    // The one that catches a proxy with WebSocket support switched off, which is
    // the most common reason a Blazor Server app goes quiet behind Nginx Proxy
    // Manager, SWAG or Cloudflare.
    const socketCell = row('WebSocket upgrade', null, 'connecting…');
    try {
        const url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/diag/ws';
        const socket = new WebSocket(url);
        const timer = setTimeout(function () {
            set(socketCell, false, 'timed out — your proxy is probably not passing WebSockets');
            socket.close();
        }, 8000);

        socket.onmessage = function (event) {
            clearTimeout(timer);
            set(socketCell, event.data === 'ok', 'open, server said "' + event.data + '"');
            socket.close();
        };
        socket.onerror = function () {
            clearTimeout(timer);
            set(socketCell, false, 'refused — your proxy is probably not passing WebSockets');
        };
    } catch (error) {
        set(socketCell, false, String(error));
    }

    // Copying works without a circuit, which is the point.
    const copy = document.getElementById('diag-copy');
    if (copy) {
        copy.addEventListener('click', function () {
            const server = document.getElementById('diag-report');
            const browser = document.getElementById('diag-browser');
            let text = (server ? server.textContent : '') + '\nBrowser\n';
            if (browser) {
                const nodes = browser.children;
                for (let i = 0; i + 1 < nodes.length; i += 2)
                    text += '  ' + nodes[i].textContent + ': ' + nodes[i + 1].textContent + '\n';
            }
            text += '  User agent: ' + navigator.userAgent + '\n';
            text += '  Page address: ' + location.href + '\n';

            navigator.clipboard.writeText(text).then(
                function () { copy.textContent = 'Copied'; },
                function () { copy.textContent = 'Could not copy — select the text instead'; });
        });
    }
})();
