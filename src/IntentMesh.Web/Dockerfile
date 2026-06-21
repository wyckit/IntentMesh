# IntentMesh Control Room — container image. Build from the REPO ROOT:
#   docker build -f src/IntentMesh.Web/Dockerfile -t intentmesh-controlroom .
#
# The TLM bundle is embedded in IntentMesh.Core, so the image is self-contained (no dataset/ needed).
# See docs/DEPLOYMENT.md for the TLS/reverse-proxy contract, required env vars, and the security model.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# Locked restore (honors packages.lock.json + the repo NuGet.config source mapping) so the image is
# built from the exact, pinned dependency set — a drifted lock file fails the build rather than
# silently resolving different packages.
RUN dotnet restore IntentMesh.slnx --locked-mode \
 && dotnet publish src/IntentMesh.Web/IntentMesh.Web.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# Run as a non-root user.
RUN useradd -u 10001 -m app
COPY --from=build /app .
USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

# Liveness probe. In Production the app REFUSES to start with the demo audit key — supply a real one:
#   docker run -e INTENTMESH_AUDIT_KEY=<base64 >=16 bytes> -e INTENTMESH_WEB_TOKEN=<token> \
#              -e "AllowedHosts=mesh.example.com" -p 8080:8080 intentmesh-controlroom
HEALTHCHECK --interval=30s --timeout=3s --retries=3 CMD wget -qO- http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "IntentMesh.Web.dll"]
