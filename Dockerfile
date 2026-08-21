# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore as its own layer so code changes do not re-download packages.
COPY MeshVault.slnx ./
COPY src/MeshVault.Core/MeshVault.Core.csproj src/MeshVault.Core/
COPY src/MeshVault.Data/MeshVault.Data.csproj src/MeshVault.Data/
COPY src/MeshVault.Web/MeshVault.Web.csproj src/MeshVault.Web/
RUN dotnet restore src/MeshVault.Web/MeshVault.Web.csproj -a "$TARGETARCH"

COPY src/ src/
RUN dotnet publish src/MeshVault.Web/MeshVault.Web.csproj \
    -c Release -a "$TARGETARCH" --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# curl is only here for the healthcheck.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    MeshVault__DataPath=/data \
    DOTNET_gcServer=0

# /data holds the SQLite database, thumbnails, viewer geometry and the data
# protection keys that keep people signed in across updates.
VOLUME ["/data"]
EXPOSE 8080

# Runs as root by default so it can read a library mounted with arbitrary
# ownership, which is the norm on Unraid. Override with `user:` in compose if
# your mounts allow it.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "MeshVault.Web.dll"]
