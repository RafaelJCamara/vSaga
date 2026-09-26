FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/VSaga.Abstractions/VSaga.Abstractions.csproj src/VSaga.Abstractions/
COPY src/VSaga.Chaos/VSaga.Chaos.csproj src/VSaga.Chaos/
COPY src/VSaga.Core/VSaga.Core.csproj src/VSaga.Core/
COPY src/VSaga.Observability/VSaga.Observability.csproj src/VSaga.Observability/
COPY src/VSaga.Persistence.EFCore/VSaga.Persistence.EFCore.csproj src/VSaga.Persistence.EFCore/
COPY src/VSaga.Persistence.Redis/VSaga.Persistence.Redis.csproj src/VSaga.Persistence.Redis/
COPY src/VSaga.Transport.Common/VSaga.Transport.Common.csproj src/VSaga.Transport.Common/
COPY src/VSaga.Transport.RabbitMQ/VSaga.Transport.RabbitMQ.csproj src/VSaga.Transport.RabbitMQ/
COPY src/VSaga.Transport.Wolverine/VSaga.Transport.Wolverine.csproj src/VSaga.Transport.Wolverine/
COPY src/VSaga.Transport.MassTransit/VSaga.Transport.MassTransit.csproj src/VSaga.Transport.MassTransit/
COPY src/VSaga.Transport.Brighter/VSaga.Transport.Brighter.csproj src/VSaga.Transport.Brighter/
COPY src/VSaga.Transport.Http/VSaga.Transport.Http.csproj src/VSaga.Transport.Http/
COPY src/VSaga.Http/VSaga.Http.csproj src/VSaga.Http/
COPY samples/VSaga.Samples.OrderProcessing.Contracts/VSaga.Samples.OrderProcessing.Contracts.csproj samples/VSaga.Samples.OrderProcessing.Contracts/
COPY samples/VSaga.Samples.OrderProcessing/VSaga.Samples.OrderProcessing.csproj samples/VSaga.Samples.OrderProcessing/
RUN dotnet restore samples/VSaga.Samples.OrderProcessing/VSaga.Samples.OrderProcessing.csproj

COPY src/ src/
COPY samples/ samples/
RUN dotnet publish samples/VSaga.Samples.OrderProcessing/VSaga.Samples.OrderProcessing.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "VSaga.Samples.OrderProcessing.dll"]
