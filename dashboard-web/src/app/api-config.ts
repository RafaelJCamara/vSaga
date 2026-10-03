// The SPA calls the API on its own origin, so every URL here is relative: the page and the API share
// a host and port, no API port is baked into the bundle, and no CORS policy is involved. In the
// compose stack nginx serves the bundle and proxies /api and /hubs to the dashboard API; under
// `ng serve` the dev server does the same through proxy.conf.mjs (VSAGA_API_URL picks the API, by
// default the compose stack's http://localhost:5080). @microsoft/signalr resolves the relative hub
// URL against the page.
export const API_BASE_URL = '';
export const HUB_URL = `${API_BASE_URL}/hubs/saga`;

// No credential lives here or anywhere in the bundle: the SPA signs in with a username and password, and
// the browser carries the session cookie (and Angular's XSRF interceptor copies the antiforgery cookie into
// a header). The API key in docker-compose.yml is for curl and other machine clients.
