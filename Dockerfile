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

# Run as the image's built-in non-root user.
USER $APP_UID

ENTRYPOINT ["dotnet", "ETMS.Api.dll"]
