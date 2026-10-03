FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/VSaga.Abstractions/VSaga.Abstractions.csproj src/VSaga.Abstractions/
COPY src/VSaga.Core/VSaga.Core.csproj src/VSaga.Core/
COPY src/VSaga.Observability/VSaga.Observability.csproj src/VSaga.Observability/
COPY src/VSaga.Persistence.EFCore/VSaga.Persistence.EFCore.csproj src/VSaga.Persistence.EFCore/
COPY src/VSaga.Persistence.EFCore.Postgres/VSaga.Persistence.EFCore.Postgres.csproj src/VSaga.Persistence.EFCore.Postgres/
COPY src/VSaga.Persistence.MongoDB/VSaga.Persistence.MongoDB.csproj src/VSaga.Persistence.MongoDB/
COPY src/VSaga.Persistence.Redis/VSaga.Persistence.Redis.csproj src/VSaga.Persistence.Redis/
COPY src/VSaga.Transport.Common/VSaga.Transport.Common.csproj src/VSaga.Transport.Common/
COPY src/VSaga.Transport.RabbitMQ/VSaga.Transport.RabbitMQ.csproj src/VSaga.Transport.RabbitMQ/
COPY src/VSaga.Transport.Http/VSaga.Transport.Http.csproj src/VSaga.Transport.Http/
COPY src/VSaga.Dashboard.Identity/VSaga.Dashboard.Identity.csproj src/VSaga.Dashboard.Identity/
COPY src/VSaga.Dashboard.Identity.Sqlite/VSaga.Dashboard.Identity.Sqlite.csproj src/VSaga.Dashboard.Identity.Sqlite/
COPY src/VSaga.Dashboard.Api/VSaga.Dashboard.Api.csproj src/VSaga.Dashboard.Api/
RUN dotnet restore src/VSaga.Dashboard.Api/VSaga.Dashboard.Api.csproj

COPY src/ src/
RUN dotnet publish src/VSaga.Dashboard.Api/VSaga.Dashboard.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# curl backs docker-compose's HEALTHCHECK against /health — order-processing depends on this container
# reporting healthy (i.e. migrations applied) before it starts, so it never begins publishing
# messages against a schema this app's MigrateAsync hasn't finished applying yet.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
# The dashboard's users, roles and data-protection keys live in one SQLite file. Its directory is created
# here, owned by the image's non-root "app" user and closed to everyone else, so a new named volume mounted
# on it takes that ownership and mode. The path is set in the image too, so a bare `docker run` writes to
# this directory rather than reporting the identity store unavailable (there is no default inside a
# container). Mount a volume on /var/lib/vsaga-dashboard or every user and key goes with the container.
ENV Dashboard__Identity__Sqlite__Path=/var/lib/vsaga-dashboard/identity.db
RUN mkdir -p /var/lib/vsaga-dashboard && chown $APP_UID /var/lib/vsaga-dashboard && chmod 700 /var/lib/vsaga-dashboard
# Run as the aspnet image's "app" user (uid 1654); curl is installed above as root, and port 8080 needs no privilege.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "VSaga.Dashboard.Api.dll"]
