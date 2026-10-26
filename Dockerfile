# syntax=docker/dockerfile:1

# ---------- Stage 1: restore, build and publish ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so the package layer is cached until the project file changes.
COPY src/ETMS.Api/ETMS.Api.csproj src/ETMS.Api/
RUN dotnet restore src/ETMS.Api/ETMS.Api.csproj

COPY src/ETMS.Api/ src/ETMS.Api/
RUN dotnet publish src/ETMS.Api/ETMS.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# ---------- Stage 2: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .

# Attachment storage (Storage:LocalStoragePath). Created up front and owned by the app user, because
# /app itself is root-owned; docker-compose mounts a named volume here, which inherits this ownership.
RUN mkdir -p /app/App_Data/Uploads && chown -R $APP_UID /app/App_Data

# Data Protection key ring (DataProtection__KeyRingPath). Same reasoning: the etms_dpkeys volume mounted here
# takes this ownership on first use, so the non-root app user can write keys.
RUN mkdir -p /var/dpkeys && chown $APP_UID /var/dpkeys && chmod 700 /var/dpkeys

# Run as the image's built-in non-root user.
USER $APP_UID

ENTRYPOINT ["dotnet", "ETMS.Api.dll"]
