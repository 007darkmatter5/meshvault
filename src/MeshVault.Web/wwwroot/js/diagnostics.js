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

    // Where the page thinks it lives. A proxy that serves the app under a path
    // prefix, or rewrites the base, sends every relative asset request to the
    // wrong place.
    const baseHref = document.querySelector('base');
    row('Page origin', true, location.origin + '  (base href "'
        + (baseHref ? baseHref.getAttribute('href') : 'none') + '")');

    // Re-request exactly what the document asked for, rather than a guessed
    // path: Blazor and MudBlazor are loaded under fingerprinted names that
    // change with every build, so a hardcoded path can fail while the real one
    // is fine, and vice versa.
    function wanted(label, selector, attribute) {
        const cell = row(label, null, 'waiting…');

        // The script tags sit at the end of the body, after this file, so the
        // document is still being parsed when this runs.
        waitFor(function () { return document.querySelector(selector) !== null; }, function (found) {
            if (!found) {
                set(cell, false, 'the page does not reference it at all');
                return;
            }
            check(cell, document.querySelector(selector)[attribute]);
        });
    }

    function check(cell, url) {
        fetch(url, { cache: 'no-store' })
            .then(function (response) {
                return response.text().then(function (body) {
                    const type = response.headers.get('content-type') || '';
                    // Two failures that both look like success at a glance: a
                    // 200 with nothing in it, and a 404 answered with the
                    // friendly not-found page instead of the asset.
                    const html = type.indexOf('text/html') !== -1;
                    const ok = response.ok && body.length > 0 && !html;

                    let detail = 'HTTP ' + response.status + ', '
                        + body.length.toLocaleString() + ' bytes, ' + (type || 'no content type');
                    if (html && !response.ok) detail += ' — an HTML error page, not the asset';
                    else if (html) detail += ' — HTML where a script or stylesheet was expected';
                    else if (response.ok && body.length === 0) detail += ' — empty';

                    // Cloudflare stamps these. A failure served from its cache
                    // is Cloudflare's copy of an old fault, not the server's
                    // answer today, and a purge fixes what a redeploy will not.
                    const cached = response.headers.get('cf-cache-status');
                    if (cached) {
                        detail += '\nCloudflare cache: ' + cached;
                        if (!response.ok && cached === 'HIT')
                            detail += ' — this failure is cached at the edge, purge it';
                    }

                    detail += '\n' + url;

                    set(cell, ok, detail);
                });
            })
            .catch(function (error) { set(cell, false, String(error) + '\n' + url); });
    }

    wanted('Blazor script URL', 'script[src*="blazor.web"]', 'src');
    wanted('MudBlazor script URL', 'script[src*="MudBlazor"]', 'src');
    wanted('MudBlazor style URL', 'link[href*="MudBlazor"]', 'href');

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
