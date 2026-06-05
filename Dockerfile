# --- Build frontend ---
FROM node:20-alpine AS frontend
WORKDIR /app/client
COPY YTPlaylistManager.client/package*.json ./
RUN npm ci
COPY YTPlaylistManager.client/ ./
RUN npm run build

# --- Build backend ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /app
COPY YTPlaylistManager.Server/ ./YTPlaylistManager.Server/
COPY YTPlaylistManager.client/ ./YTPlaylistManager.client/
WORKDIR /app/YTPlaylistManager.Server
RUN dotnet publish YTPlaylistManager.Server.csproj -c Release -o /app/publish

# --- Runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=backend /app/publish ./
COPY --from=frontend /app/client/dist/yt-playlist-manager/browser ./wwwroot
EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENTRYPOINT ["dotnet", "YTPlaylistManager.Server.dll"]
