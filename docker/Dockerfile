# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so the layer is cached until a project file changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/RuleEngine.Domain/RuleEngine.Domain.csproj src/RuleEngine.Domain/
COPY src/RuleEngine.Application/RuleEngine.Application.csproj src/RuleEngine.Application/
COPY src/RuleEngine.Infrastructure/RuleEngine.Infrastructure.csproj src/RuleEngine.Infrastructure/
COPY src/RuleEngine.Api/RuleEngine.Api.csproj src/RuleEngine.Api/
RUN dotnet restore src/RuleEngine.Api/RuleEngine.Api.csproj

COPY src/ src/
RUN dotnet publish src/RuleEngine.Api/RuleEngine.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# curl is only used by the container health check.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

COPY --from=build /app/publish .

USER app
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "RuleEngine.Api.dll"]
