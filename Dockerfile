# =====================================================================
# Limitless Activation Server — Production Dockerfile
# =====================================================================
# Two-stage build: SDK image compiles, runtime image runs. Final image
# is ~100MB instead of ~700MB.
#
# Targets ASP.NET Core 8 on Linux. The server is fully cross-platform
# (no Windows-only dependencies — the csproj does NOT reference LTC.Core
# which contains the mtapi.mt5.dll Windows binding).
#
# Railway picks this up automatically when present in the repo root.
# =====================================================================

# ---------- BUILD STAGE ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src

# Copy csproj first and restore — leverages Docker layer cache so we
# don't re-download NuGet packages on every code change.
COPY LTC.Server/LTC.Server.csproj LTC.Server/
RUN dotnet restore LTC.Server/LTC.Server.csproj

# Now copy the rest of the server source and publish.
COPY LTC.Server/ LTC.Server/

# Publish in Release mode. Self-contained=false because the runtime
# image already has .NET 8 installed (smaller image, faster pulls).
RUN dotnet publish LTC.Server/LTC.Server.csproj \
      -c Release \
      -o /app/publish \
      --no-restore \
      /p:UseAppHost=false

# ---------- RUNTIME STAGE ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

WORKDIR /app

# Copy the published output from the build stage.
COPY --from=build /app/publish .

# Create directories for the SQLite DB and logs. Railway's persistent
# volume will be mounted at /data so the DB survives container restarts.
RUN mkdir -p /data /app/logs

# Environment variables — defaults can be overridden via Railway dashboard.
# ASP.NET Core reads these automatically through IConfiguration.
# Railway sets PORT dynamically (usually 8080); we tell Kestrel to bind to it.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV Database__Path=/data/licenses.db
ENV Signing__PrivateKeyPath=/run/secrets/keygen-private.key

# Railway expects the container to listen on $PORT.
EXPOSE 8080

# Run the server.
ENTRYPOINT ["dotnet", "LimitlessActivationServer.dll"]
