# HomeLab Pulse dashboard

Angular PWA for the public HomeLab Pulse REST and SignalR endpoints.

## Local development

Run the .NET receiver on loopback port 5080 and configure an approved synthetic node using the [backend instructions](../backend/README.md). From this directory, run `npm ci` and `npm start`. The development server forwards the public `/api` and `/hubs` routes to the backend.

Run `npm test -- --watch=false`, `npm run check:format`, and `npm run build` before publishing changes. The production output is in `dist/pulse-dashboard/browser`.

## Public data

The dashboard reads the public overview, machine, service, and fleet history endpoints. It applies newer overview revisions from REST and SignalR and resynchronizes after reconnect. Polling keeps REST data available when the live transport is interrupted. The service worker caches the application shell and assets. API and hub traffic remains network-only.

Missing measurements display an em dash, and the interface marks stale readings. The Engineering page links to documented practices and reports the absence of verified operational outcomes.

Deploy under the same origin as the public REST and SignalR routes. The private ingest route stays on WireGuard.
