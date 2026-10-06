# Vendored browser assets

- Bootstrap 5.3.8: `bootstrap/dist/css/bootstrap.min.css` (MIT, see bootstrap.LICENSE).
- Microsoft SignalR 10.0.0: `@microsoft/signalr/dist/browser/signalr.min.js` (MIT, see signalr.LICENSE).

Installed from npm with scripts disabled. No CDN dependency at runtime. Upgrade with npm pinned versions, copy the distribution files and corresponding licenses, then run the browser smoke test. The unused source-map comments are intentionally removed to avoid devtools requests for unpublished map files.
