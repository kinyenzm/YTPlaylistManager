# --- Build frontend ---
FROM node:24-alpine AS frontend
WORKDIR /app/client
COPY YTPlaylistManager.client/package*.json ./
RUN npm ci
COPY YTPlaylistManager.client/ ./
RUN npm run build

# --- Build backend ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /app
# Solo el servidor: el .csproj referencia el .esproj de Angular (para F5 en Visual Studio)
# y, si la carpeta del cliente está, dotnet publish intenta compilarlo y exige Node.js,
# que esta imagen no trae. El frontend ya lo compila la etapa anterior; sin la carpeta,
# MSBuild solo avisa (MSB9008) y publica el backend.
COPY YTPlaylistManager.Server/ ./YTPlaylistManager.Server/
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
