# YT Playlist Manager

Herramienta personal full stack (**ASP.NET Core .NET 10** + **Angular 22** — standalone, zoneless, signals, i18n es/en) para gestionar tus playlists de YouTube/YouTube Music: listar, **quitar repetidas**, **unir playlists** (local-first, con subida controlada a YouTube) y **ordenar canciones con IA**.

Importante: YouTube Data API funciona igual con o sin Premium; **YouTube Premium no aporta funciones extra** para esta herramienta, pero tampoco te limita. Lo único que necesitas es una cuenta de Google con tus playlists.

---

## Estructura del proyecto

Arquitectura en capas (estilo MVC), con backend y frontend como proyectos separados al root:

```
YTPlaylistManager/
├── YTPlaylistManager.Server/      # Backend ASP.NET Core (.NET 10)
│   ├── Controllers/              # Auth, Playlists, Songs, Cache, Analysis, Operations
│   ├── Domain/Entities/          # tokens, log, merge-review, pending-upload, ...
│   ├── DTOs/                     # DTOs.cs (records request/response)
│   ├── Services/                 # YouTubeService, SongSearchService, NvidiaClassifier,
│   │                             #   stores JSON (cache de listas/items, pendientes, archivadas)
│   ├── Filters/                  # RequireGoogleSession
│   ├── Middleware/               # GlobalExceptionMiddleware
│   ├── Program.cs
│   └── YTPlaylistManager.Server.csproj
├── YTPlaylistManager.client/      # Frontend Angular 22 (standalone, zoneless, signals)
│   └── src/
│       ├── assets/i18n/          # es.json / en.json (ngx-translate)
│       ├── environments/         # environment.ts / environment.production.ts (apiBaseUrl)
│       ├── proxy.conf.js         # proxy /api → backend en dev
│       └── app/
│           ├── services/         # api.service.ts
│           ├── models/           # models.ts
│           ├── components/       # song-search, cache-explorer, lang-switcher
│           └── pages/            # playlists, playlist-detail, cross-duplicates
├── Dockerfile                     # build client + backend, sirve el SPA desde wwwroot
└── YTPlaylistManager.slnx         # Solución (formato XML)
```

> **Sin base de datos:** la persistencia es JSON local (token OAuth, cache de listas/items, uniones pendientes, logs); el resto del estado vive en la YouTube Data API. Por eso no hay capa `Data/` (EF Core).

---

## 0) Configuración (appsettings)

El repo **no incluye** `appsettings.json` (lleva tus claves, está en `.gitignore`). Copia la plantilla y rellena tus valores:

```bash
cp YTPlaylistManager.Server/appsettings.test.json YTPlaylistManager.Server/appsettings.json
```

Luego editá `appsettings.json` con tus credenciales de **Google** (sección 1) y tu API key de **NVIDIA** (sección 2). Tus claves quedan locales, no se suben al repo.

---

## 1) Credenciales Google (obligatorio)

1. Entra a https://console.cloud.google.com/ y crea (o reutiliza) un proyecto.
2. Habilita la **YouTube Data API v3**.
3. Configura la **OAuth consent screen** (External, modo Testing es suficiente para uso personal). Agrega tu propio correo como **Test user**.
4. Crea **OAuth client ID** → tipo *Web application*.
   - Authorized redirect URI: `http://localhost:5080/api/auth/callback`
5. Copia `Client ID` y `Client Secret` y pégalos en `YTPlaylistManager.Server/appsettings.json` bajo `Google`.

**Scopes usados:**
- `https://www.googleapis.com/auth/youtube` (escribir: borrar items, crear playlists, agregar items)
- `https://www.googleapis.com/auth/youtube.readonly` (leer)

---

## 2) Proveedor de IA para clasificación (NVIDIA)

**Para qué se usa:** el botón **"Clasificar con IA"** (dentro de una playlist) agrupa las canciones por **género / mood / década**. Lo resuelve un LLM hospedado en **NVIDIA NIM** (`build.nvidia.com`, tier gratis). Si **no** configurás la API key, cae a un **fallback heurístico** (agrupa por canal) — la app funciona igual, solo sin IA.

**Cómo obtener la API key (gratis):**
1. Entrá a https://build.nvidia.com/ y creá cuenta (NVIDIA Developer).
2. Elegí un modelo (ej. `meta/llama-3.3-70b-instruct`) → **Get API Key** → copiá el token `nvapi-...`.
3. Pegalo en `appsettings.json` bajo `Ai`:

```json
"Ai": {
  "Provider": "nvidia",
  "NvidiaApiKey": "nvapi-XXXXXXXXXXXXXXXX",
  "NvidiaModel": "meta/llama-3.3-70b-instruct",
  "NvidiaBaseUrl": "https://integrate.api.nvidia.com/v1/"
}
```

La API de NVIDIA es **compatible con OpenAI Chat Completions**, así que el mismo `NvidiaClassifier.cs` funciona con cualquier endpoint OpenAI-compatible (Together.ai, Groq, vLLM local, LM Studio, Ollama con `/v1`, etc.) — solo cambia `NvidiaBaseUrl` y el modelo.

---

## 3) Ejecutar el backend

```bash
cd YTPlaylistManager.Server
dotnet restore
dotnet run
```

Abrirá la UI **Scalar** en `http://localhost:5080/scalar/v1` (documento OpenAPI nativo de .NET 10 en `http://localhost:5080/openapi/v1.json`).

---

## 4) Ejecutar el frontend

```bash
cd YTPlaylistManager.client
npm install
npm start
```

Abre `http://localhost:4200`. El `proxy.conf.js` redirige `/api` → `http://localhost:5080`, así no hace falta tocar CORS en dev.

---

## 5) Flujo

1. **Conectar con Google** → consent → vuelve con sesión activa. Verás tus listas.
2. **Quitar repetidas** (dentro de una lista): *Buscar repetidas* → *Eliminar* (por *mismo video* o *mismo título*).
3. **Unir listas** (en *Mis listas*), modelo **local-first**:
   - Marcá 2+ listas → **Revisar y unir** → vista previa (qué canciones se agregan, **una fila por canción** con sus listas de origen) → **Aplicar**.
   - La unión se aplica **en local** (0 cuota) y queda **pendiente de subir**; aparece un banner. Las listas origen quedan **🕒 en cola** (marcadas y bloqueadas). Si las listas ya están contenidas en la destino, se unen igual para **borrar las repetidas**.
   - **Subir a YouTube**: inserta las canciones en la lista destino y **borra las listas origen** de tu cuenta. Es **parcial y reanudable**: si se agota la cuota diaria, continúa al día siguiente desde donde quedó.
   - **Descartar** revierte la unión local sin tocar YouTube.
4. **Organizar canciones** (menú *Organizar*, ruta `/organizar`), también **local-first**. Tres modos:
   - **Repetidas**: las canciones que están en 2+ listas.
   - **Por lista**: elegís una lista y ves todas sus canciones; podés seleccionar varias y **quitarlas** de esa lista de una.
   - **Por canción**: buscás por nombre o ID.
   - En cualquier modo, por canción abrís un selector con **todas tus listas** (marcadas donde está ahora) y elegís dónde debe quedar — **en varias o en una sola**. Se agrega a las nuevas y se quita de las desmarcadas. Todo queda **pendiente de subir** y se sube/parcial igual que las uniones.
5. **Ordenar con IA** (dentro de una lista): por *género / ánimo / década*.
6. **Idioma**: selector **ES / EN** arriba (autodetecta el del navegador). Las rutas existen en ambos idiomas (`/organizar` ↔ `/organize`, `/buscar` ↔ `/search`, `/datos` ↔ `/data`, `/listas/:id` ↔ `/playlists/:id`). Casi todo funciona **offline** desde la cache (0 cuota); solo *Actualizar todo* y *Subir a YouTube* usan la API.

---

## Endpoints REST principales

| Método | Ruta | Descripción |
|--------|------|-------------|
| GET    | `/api/auth/login` | Inicia OAuth |
| GET    | `/api/auth/callback` | Callback OAuth (uso interno) |
| GET    | `/api/auth/status` | ¿Hay sesión activa? |
| POST   | `/api/auth/logout` | Borra token local |
| GET    | `/api/playlists` | Lista tus listas (cache; `?refresh` para releer) |
| GET    | `/api/playlists/{id}/items` | Canciones de una lista |
| GET    | `/api/playlists/{id}/duplicates` | Repetidas dentro de una lista |
| GET    | `/api/playlists/cross-duplicates` | Repetidas entre listas |
| POST   | `/api/playlists/remove-duplicates` | Elimina repetidas |
| POST   | `/api/playlists/merge` | Une en local → deja pendiente de subir |
| POST   | `/api/playlists/merge/preview` | Vista previa de la unión (0 cuota) |
| GET    | `/api/playlists/pending-uploads` | Cambios pendientes de subir |
| POST   | `/api/playlists/pending-uploads/{id}/upload` | Sube a YouTube (+ borra listas origen) |
| DELETE | `/api/playlists/pending-uploads/{id}` | Descarta y revierte la unión local |
| POST   | `/api/playlists/refresh-all` | Relee todas las listas desde YouTube |
| POST   | `/api/playlists/{id}/classify` | Ordena con IA |
| POST   | `/api/songs/search` | Busca canciones (videoId/nombre, en cache) |
| GET    | `/api/songs/{videoId}/locations` | Listas donde está la canción (cache, 0 cuota) |
| POST   | `/api/songs/assign` | Asigna la canción a un set de listas (agrega/quita) → pendiente |
| POST   | `/api/songs/remove-from-playlist` | Quita varias canciones de una lista → pendiente |
| GET    | `/api/songs/pending-moves` | Reasignaciones de canciones pendientes de subir |
| POST   | `/api/songs/pending-moves/{id}/upload` | Sube a YouTube la reasignación (parcial/reanudable) |
| DELETE | `/api/songs/pending-moves/{id}` | Descarta y revierte la reasignación local |
| GET/POST | `/api/cache/*` | Explorar la cache local |
| GET    | `/api/operations` | Log de operaciones realizadas |

Los errores se manejan de forma central en `GlobalExceptionMiddleware`: `401` sin sesión Google, `400` petición inválida, `502` fallo de servicio externo, `500` resto.

---

## Persistencia

Todo vive en `YTPlaylistManager.Server/data/` (ya ignorado en `.gitignore`):

- `google-token.json` → tokens OAuth.
- `playlist-cache.json` / `items-cache.json` → cache local de listas y canciones (permite trabajar **offline** sin gastar cuota; la cache de items resuelve aunque cambie el token).
- `pending-uploads.json` → uniones aplicadas en local **pendientes de subir** a YouTube (sobreviven reinicios → la subida es reanudable).
- `operations.json` / `merge-reviews.json` / `archived-playlists.json` → logs y registro.

---

## Despliegue con Docker

El `Dockerfile` compila el frontend, publica el backend y sirve el Angular ya compilado desde `wwwroot` del backend (un solo contenedor, mismo origen → no hace falta CORS ni proxy en prod):

```bash
docker build -t ytplaylistmanager .
docker run -p 8080:8080 ytplaylistmanager
```

La app queda en `http://localhost:8080`. Ajusta la `Authorized redirect URI` en Google y `Google:RedirectUri` al host de producción.

---

## Notas de diseño

- **Detección de duplicados** en dos niveles: por `videoId` (exacto) y por **título normalizado** (quita paréntesis, "official video", acentos, etc.) — capta "misma canción subida por canales distintos".
- **Cuotas API**: YouTube Data API tiene 10.000 unidades/día por defecto. Listar es barato (1 unidad), insertar/borrar items cuesta ~50.
- **Reordenar**: la API soporta `playlistItems.update` con `position`. No expuesto en la UI todavía, pero el servicio está preparado para extenderlo.

---

## Seguridad

Esta herramienta es para **uso local personal**. No despliegues el backend en un servidor público sin antes:
- Encriptar el almacén de tokens.
- Manejar refresh de tokens robusto.
- Limitar CORS a tu propio dominio.
- Usar HTTPS.
