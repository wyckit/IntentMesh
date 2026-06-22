# IntentMesh Control Room — container image. Build from the REPO ROOT:
#   docker build -f src/IntentMesh.Web/Dockerfile -t intentmesh-controlroom .
#
# The TLM bundle is embedded in IntentMesh.Core, so the image is self-contained (no dataset/ needed).
# See docs/DEPLOYMENT.md for the TLS/reverse-proxy contract, required env vars, and the security model.
# Base images are DIGEST-pinned (not floating :10.0 tags) for reproducible, tamper-evident inputs;
# a .dockerignore keeps local build outputs/secrets/runs out of the context (which uses COPY . .).

# mcr.microsoft.com/dotnet/sdk:10.0
FROM mcr.microsoft.com/dotnet/sdk@sha256:548d93f8a18a1acbe6cc127bc4f47281430d34a9e35c18afa80a8d6741c2adc3 AS build
WORKDIR /src
COPY . .
# Locked restore (honors packages.lock.json + the repo NuGet.config source mapping) so the image is
# built from the exact, pinned dependency set — a drifted lock file fails the build rather than
# silently resolving different packages.
RUN dotnet restore IntentMesh.slnx --locked-mode \
 && dotnet publish src/IntentMesh.Web/IntentMesh.Web.csproj -c Release --no-restore -o /app

# mcr.microsoft.com/dotnet/aspnet:10.0
FROM mcr.microsoft.com/dotnet/aspnet@sha256:ddcf70ad1ab963a4fcd41fbd722a6b660e404e87567cfbd46fd2809c21b02088 AS runtime
WORKDIR /app
# Run as a non-root user, and pre-create the runs volume mount point owned by that user so persistence
# works with a named volume (the app writes signed bundles + a /readyz write-probe there).
# Non-root user + the runs volume mount point (owned by that user), and curl for the HEALTHCHECK — the
# base aspnet image ships neither wget nor curl, so the probe tool must be installed explicitly.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 && useradd -u 10001 -m app \
 && mkdir -p /data/runs \
 && chown -R 10001:10001 /data/runs
COPY --from=build /app .
USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    INTENTMESH_RUNS_DIR=/data/runs
EXPOSE 8080
VOLUME ["/data/runs"]

# Readiness probe: /readyz WRITE-PROBES the runs dir (write + atomic move + delete), so an unwritable or
# unmounted volume is reported unhealthy — not a bare liveness ping. In Production the app REFUSES to
# start with the demo audit key or without an auth boundary — supply real values:
#   docker run -e INTENTMESH_AUDIT_KEY=<base64 >=16 bytes> -e INTENTMESH_AUTH_KEY=<base64 >=16 bytes> \
#              -e INTENTMESH_PRINCIPALS=/run/secrets/principals.json -e "AllowedHosts=mesh.example.com" \
#              -v intentmesh-runs:/data/runs -p 8080:8080 intentmesh-controlroom
HEALTHCHECK --interval=30s --timeout=3s --retries=3 CMD curl -fsS http://localhost:8080/readyz || exit 1

ENTRYPOINT ["dotnet", "IntentMesh.Web.dll"]
