// Kept deliberately simple (no environment-file build variants) for a v1 sample dashboard: the
// browser talks to Dashboard.Api directly on its host-exposed port, which these defaults assume is
// the docker-compose stack's published one. A locally run API (`dotnet run`) listens on
// launchSettings.json's http://localhost:5275 instead and ships an empty Dashboard:ApiKey (every
// request fails closed), so it needs Dashboard__ApiKey set to the value below plus either this URL
// edited or the API started on port 5080.
export const API_BASE_URL = 'http://localhost:5080';
export const HUB_URL = `${API_BASE_URL}/hubs/saga`;

// Matches docker-compose.yml's Dashboard__ApiKey dev value. A key embedded in a compiled SPA bundle is
// visible to anyone with devtools — this only closes off unauthenticated direct API access, it is not
// per-user auth. Change both places together if you change this.
export const DASHBOARD_API_KEY = 'dev-local-only-change-me';
