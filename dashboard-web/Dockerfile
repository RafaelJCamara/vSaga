FROM node:22-bookworm-slim AS build
WORKDIR /src
ENV CI=true NG_CLI_ANALYTICS=false
# Manifests before sources, so npm ci stays cached while the SPA or the nginx template changes.
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY angular.json tsconfig*.json ./
COPY public/ public/
COPY src/ src/
RUN npx ng build

FROM nginxinc/nginx-unprivileged:1.30-alpine-slim AS runtime
# The base image's entrypoint renders /etc/nginx/templates/*.template into conf.d at start, substituting
# only the variables NGINX_ENVSUBST_FILTER names (nginx's own $variables are left alone), and exports
# NGINX_LOCAL_RESOLVERS from /etc/resolv.conf. No shell script of our own: a CRLF checkout would break it.
# DASHBOARD_API_UPSTREAM: host:port of the dashboard API (the compose service name by default).
# DASHBOARD_OUTER_PROXY: true only when a TLS terminator in front sets X-Forwarded-Proto; otherwise nginx
# sends its own scheme and a client cannot claim https.
ENV DASHBOARD_API_UPSTREAM=dashboard-api:8080 \
    DASHBOARD_OUTER_PROXY=false \
    NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1 \
    NGINX_ENVSUBST_FILTER="^(DASHBOARD_API_UPSTREAM|DASHBOARD_OUTER_PROXY|NGINX_LOCAL_RESOLVERS)$"
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /src/dist/dashboard-web/browser/ /srv/dashboard-web/
COPY --from=build /src/dist/dashboard-web/3rdpartylicenses.txt /usr/share/licenses/dashboard-web/
EXPOSE 8080
# The base image stops with SIGQUIT (graceful), which makes docker compose down wait out open WebSockets.
STOPSIGNAL SIGTERM
