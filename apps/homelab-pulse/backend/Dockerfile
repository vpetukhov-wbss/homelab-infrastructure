# Build context: apps/homelab-pulse (the API embeds ../contracts).
# The SDK tag must match global.json, which disables roll-forward.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble AS build
WORKDIR /src/backend
COPY backend/global.json backend/Directory.Build.props backend/Directory.Packages.props ./
COPY backend/src/HomeLab.Pulse.Api/HomeLab.Pulse.Api.csproj backend/src/HomeLab.Pulse.Api/packages.lock.json src/HomeLab.Pulse.Api/
RUN dotnet restore --locked-mode src/HomeLab.Pulse.Api/HomeLab.Pulse.Api.csproj
COPY contracts /src/contracts
COPY backend/.editorconfig ./
COPY backend/src src
RUN dotnet publish src/HomeLab.Pulse.Api/HomeLab.Pulse.Api.csproj \
    --configuration Release --no-restore --output /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble
WORKDIR /app
COPY --from=build /out .
# A new named volume mounted on /data inherits this owner.
RUN mkdir /data && chown "$APP_UID" /data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "HomeLab.Pulse.Api.dll"]
