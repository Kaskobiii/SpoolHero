FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY SpoolManager.sln ./
COPY global.json ./
COPY src/SpoolManager.Shared/SpoolManager.Shared.csproj src/SpoolManager.Shared/
COPY src/SpoolManager.Infrastructure/SpoolManager.Infrastructure.csproj src/SpoolManager.Infrastructure/
COPY src/SpoolManager.Client/SpoolManager.Client.csproj src/SpoolManager.Client/
COPY src/SpoolManager.Server/SpoolManager.Server.csproj src/SpoolManager.Server/
RUN dotnet restore

COPY src/ src/
RUN dotnet publish src/SpoolManager.Server/SpoolManager.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ARG REVISION=unknown
ENV SPOOLHERO_REVISION=$REVISION
LABEL org.opencontainers.image.revision=$REVISION

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5000

HEALTHCHECK --interval=10s --timeout=5s --start-period=60s --retries=12 \
    CMD curl -fsS http://localhost:5000/api/local/session || exit 1

ENTRYPOINT ["dotnet", "SpoolManager.Server.dll"]
