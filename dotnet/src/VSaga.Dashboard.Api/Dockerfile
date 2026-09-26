FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/VSaga.Abstractions/VSaga.Abstractions.csproj src/VSaga.Abstractions/
COPY src/VSaga.Core/VSaga.Core.csproj src/VSaga.Core/
COPY src/VSaga.Observability/VSaga.Observability.csproj src/VSaga.Observability/
COPY src/VSaga.Persistence.EFCore/VSaga.Persistence.EFCore.csproj src/VSaga.Persistence.EFCore/
COPY src/VSaga.Persistence.EFCore.Postgres/VSaga.Persistence.EFCore.Postgres.csproj src/VSaga.Persistence.EFCore.Postgres/
COPY src/VSaga.Persistence.Redis/VSaga.Persistence.Redis.csproj src/VSaga.Persistence.Redis/
COPY src/VSaga.Transport.Common/VSaga.Transport.Common.csproj src/VSaga.Transport.Common/
COPY src/VSaga.Transport.RabbitMQ/VSaga.Transport.RabbitMQ.csproj src/VSaga.Transport.RabbitMQ/
COPY src/VSaga.Transport.Http/VSaga.Transport.Http.csproj src/VSaga.Transport.Http/
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
EXPOSE 8080
ENTRYPOINT ["dotnet", "VSaga.Dashboard.Api.dll"]
