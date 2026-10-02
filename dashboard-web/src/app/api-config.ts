// The SPA calls the API on its own origin, so every URL here is relative: the page and the API share
// a host and port, no API port is baked into the bundle, and no CORS policy is involved. In the
// compose stack nginx serves the bundle and proxies /api and /hubs to the dashboard API; under
// `ng serve` the dev server does the same through proxy.conf.mjs (VSAGA_API_URL picks the API, by
// default the compose stack's http://localhost:5080). @microsoft/signalr resolves the relative hub
// URL against the page.
export const API_BASE_URL = '';
export const HUB_URL = `${API_BASE_URL}/hubs/saga`;

// Matches docker-compose.yml's Dashboard__ApiKey dev value. A key embedded in a compiled SPA bundle is
// visible to anyone with devtools — this only closes off unauthenticated direct API access, it is not
// per-user auth. Change both places together if you change this.
export const DASHBOARD_API_KEY = 'dev-local-only-change-me';
