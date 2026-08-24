# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH

# Stamped into the assembly so the diagnostics page can name the exact commit
# it is running. The build context has no .git directory, so nothing else can
# work this out, and "which build am I on" is the first question every report
# has to answer.
ARG SOURCE_COMMIT=unknown
WORKDIR /src

# Restore as its own layer so code changes do not re-download packages.
COPY MeshVault.slnx ./
COPY src/MeshVault.Core/MeshVault.Core.csproj src/MeshVault.Core/
COPY src/MeshVault.Data/MeshVault.Data.csproj src/MeshVault.Data/
COPY src/MeshVault.Web/MeshVault.Web.csproj src/MeshVault.Web/
RUN dotnet restore src/MeshVault.Web/MeshVault.Web.csproj

COPY src/ src/

# Plain, portable, framework-dependent publish — the same command that is run
# and verified locally.
#
# This used to pass `-a $TARGETARCH --no-restore` and produced an image with no
# wwwroot/_framework in it at all, though neither flag reproduced that on a
# local publish, so this is removal of the difference rather than a diagnosis.
#
# Both were wrong here regardless. `-a` is Microsoft's cross-compilation
# pattern and belongs with `FROM --platform=$BUILDPLATFORM`; without that pin
# buildx already runs this stage as the target architecture, so the flag asked
# for a second, redundant retarget. And the split restore ran without
# -c Release, leaving publish to build a configuration it had not restored.
#
# A portable publish runs on either architecture unchanged, and the check below
# proves the output is complete rather than trusting that it is.
RUN dotnet publish src/MeshVault.Web/MeshVault.Web.csproj \
    -c Release -o /app \
    -p:SourceRevisionId="$SOURCE_COMMIT"

# blazor.web.js is what turns the rendered HTML into a working application.
# Without it every page still loads and looks perfect, and nothing on it
# responds to a click — there is no error, anywhere, at any point.
#
# An image shipped without it. Publishing it is not optional and not
# conditional, so the build now proves it happened rather than assuming it,
# and prints what it found when it did not.
RUN echo "SDK: $(dotnet --version), TARGETARCH: ${TARGETARCH}" \
    && if [ ! -s /app/wwwroot/_framework/blazor.web.js ]; then \
         echo "FATAL: blazor.web.js is missing from the publish output."; \
         echo "--- /app/wwwroot ---";            ls -la /app/wwwroot || true; \
         echo "--- /app/wwwroot/_framework ---"; ls -la /app/wwwroot/_framework || true; \
         echo "--- static asset manifest ---"; \
         grep -o '"Route":"_framework[^"]*"' /app/*.staticwebassets.endpoints.json | sort -u || true; \
         exit 1; \
       fi \
    && echo "blazor.web.js: $(stat -c%s /app/wwwroot/_framework/blazor.web.js) bytes"

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
