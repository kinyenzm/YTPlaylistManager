# YT Playlist Manager

Herramienta personal full stack (**ASP.NET Core .NET 10** + **Angular 22** — standalone, zoneless, signals, i18n es/en) para gestionar tus playlists de YouTube/YouTube Music: listar, **quitar repetidas**, **unir playlists** (local-first, con subida controlada a YouTube) y **ordenar canciones con IA**.

---

## Desarrollo con IA

Este proyecto se desarrolla íntegramente con [Claude Code](https://claude.ai/code). Atribución por etapa:

| Etapa | Modelo | Período | Alcance |
|-------|--------|---------|---------|
| Arquitectura base + features principales | **Claude Fable 5** | hasta jun 10 2026 | Stack completo: backend .NET 10, frontend Angular 22, OAuth, cache local, organizador, panel pendientes, feed SSE, diseño "midnight studio" |
| UX / bugs / refactor CSS | **Claude Sonnet 4.6** | jun 16 2026 | Command palette Ctrl+K, skeleton loading, progreso de subida en tiempo real, fix listas especiales YouTube, BEM en CSS, eliminación de inline styles, navegación vía router state |

---

## Estructura del proyecto

Arquitectura en capas (estilo MVC), con backend y frontend como proyectos separados al root:

```
YTPlaylistManager/
├── YTPlaylistManager.Server/      # Backend ASP.NET Core (.NET 10)
│   ├── Controllers/              # Auth, Playlists, Songs, Cache, Analysis, Activity
│   ├── Domain/Entities/          # tokens, log, merge-review, pending-upload, ...
│   ├── DTOs/                     # DTOs.cs (records request/response)
│   ├── Services/                 # YouTubeService, SongSearchService, NvidiaClassifier,
│   │                             #   ActivityBroadcaster (log de actividad en YouTube)
│   │                             #   stores JSON (cache de listas/items, pendientes, archivadas)
│   ├── Filters/                  # RequireGoogleSession
│   ├── Middleware/               # GlobalExceptionMiddleware
│   ├── Program.cs
│   └── YTPlaylistManager.Server.csproj
├── YTPlaylistManager.client/      # Frontend Angular 22 (standalone, zoneless, signals)
│   └── src/
│       ├── assets/
│       │   ├── i18n/             # es.json / en.json (ngx-translate, paridad 247 keys)
│       │   └── favicon.svg       # icono SVG (botón play lima sobre fondo oscuro)
│       ├── environments/         # environment.ts / environment.production.ts (apiBaseUrl)
│       ├── proxy.conf.js         # proxy /api → backend en dev
│       └── app/
│           ├── services/         # api.service.ts, pending.service.ts
│           ├── models/           # models.ts
│           ├── components/       # cache-explorer, lang-switcher, pending-changes,
│           │                     #   command-palette (Ctrl+K)
│           └── pages/            # playlists, cross-duplicates (incluye Por lista y Por canción)
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
dotnet run --launch-profile http
```

Abrirá la UI **Scalar** en `http://localhost:5080/scalar/v1` (documento OpenAPI nativo de .NET 10 en `http://localhost:5080/openapi/v1.json`).

---

## 4) Ejecutar el frontend

```bash
cd YTPlaylistManager.client
npm install
npm start
```

Abre `http://localhost:4200` automáticamente. El `proxy.conf.js` redirige `/api` → `http://localhost:5080`, así no hace falta tocar CORS en dev.

---

## 5) Flujo

1. **Conectar con Google** → consent → vuelve con sesión activa. Verás tus listas con **skeleton loading** mientras cargan.
2. **Quitar repetidas** (dentro de una lista): *Buscar repetidas* → elige *Quitar* o *Dejar este* en cada grupo. Los duplicados se priorizan: **mismo título primero**, mismo video después. Los cambios van a la **cola global de subidas**.
3. **Unir listas** (en *Mis listas*), modelo **local-first**:
   - Hacé clic en 2+ listas (toda la card es cliqueable — estado seleccionado claramente distinguido con borde lima + checkmark) → **Revisar y unir** → vista previa → **Aplicar**.
   - La unión se aplica **en local** (0 cuota) y queda **pendiente de subir**; aparece la burbuja flotante de cambios pendientes. Las listas origen quedan marcadas como *en cola*.
   - **Subir a YouTube**: inserta las canciones en la lista destino y **borra las listas origen** de tu cuenta. Es **parcial y reanudable**: si se agota la cuota diaria, continúa al día siguiente desde donde quedó. El overlay muestra **cada canción subida en tiempo real** con ✓.
   - **Listas especiales de YouTube** (Favoritos `FL`, Ver más tarde `WL`, `LL`, `RD`): se detectan automáticamente y se omite su borrado vía API (YouTube no lo permite), evitando que la unión quede bloqueada para siempre.
   - **Descartar** revierte la unión local sin tocar YouTube.
4. **Organizar canciones** (menú *Organizar*, ruta `/organizar`), también **local-first**. Tres modos:
   - **Repetidas**: las canciones que están en 2+ listas.
   - **Por lista**: elegís una lista y ves todas sus canciones; podés seleccionar varias y **quitarlas** de esa lista de una. Muestra skeleton loading mientras carga.
   - **Por canción**: buscás por nombre o ID. Muestra skeleton loading mientras busca.
   - En cualquier modo, por canción abrís un selector con **todas tus listas** (marcadas donde está ahora) y elegís dónde debe quedar — **en varias o en una sola**. Se agrega a las nuevas y se quita de las desmarcadas. Todo queda **pendiente de subir**.
5. **Búsqueda rápida — Ctrl+K**: abre el command palette desde cualquier pantalla (también vía botón en la navbar). Busca en local sin cuota:
   - **Playlists** por nombre (filtro client-side instantáneo).
   - **Canciones** por nombre, videoId parcial o canal (búsqueda en cache vía API).
   - Seleccionar una canción abre directamente el modo **Por canción** pre-buscado.
   - Navegar con ↑↓, confirmar con ↵, cerrar con Esc.
6. **Cambios pendientes — panel global**: una burbuja en la esquina inferior izquierda agrupa *todas* las subidas pendientes (uniones y reasignaciones de canciones) de cualquier pantalla. Podés subir o descartar de forma individual o en bloque desde ahí. El log completo de actividad real en YouTube queda en **Historial → Actividad**.
7. **Ordenar con IA** (dentro de una lista): por *género / ánimo / década*.
8. **Idioma**: selector **ES / EN** arriba (autodetecta el del navegador). Las rutas existen en ambos idiomas (`/organizar` ↔ `/organize`, `/datos` ↔ `/data`). Casi todo funciona **offline** desde la cache (0 cuota); solo *Actualizar todo* y *Subir a YouTube* usan la API.

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
| GET    | `/api/playlists/{id}/duplicates` | Repetidas dentro de una lista (siempre desde YouTube) |
| GET    | `/api/playlists/cross-duplicates` | Repetidas entre listas |
| POST   | `/api/playlists/remove-duplicates` | Elimina repetidas |
| POST   | `/api/playlists/merge` | Une en local → deja pendiente de subir |
| POST   | `/api/playlists/merge/preview` | Vista previa de la unión (0 cuota) |
| GET    | `/api/playlists/pending-uploads` | Cambios pendientes de subir |
| POST   | `/api/playlists/pending-uploads/{id}/upload` | Sube a YouTube (+ borra listas origen si no son especiales) |
| DELETE | `/api/playlists/pending-uploads/{id}` | Descarta y revierte la unión local |
| POST   | `/api/playlists/refresh-all` | Relee todas las listas desde YouTube |
| POST   | `/api/playlists/{id}/classify` | Ordena con IA |
| POST   | `/api/songs/search` | Busca canciones (videoId/nombre/canal, en cache) |
| GET    | `/api/songs/{videoId}/locations` | Listas donde está la canción (cache, 0 cuota) |
| POST   | `/api/songs/assign` | Asigna la canción a un set de listas (agrega/quita) → pendiente |
| POST   | `/api/songs/remove-from-playlist` | Quita varias canciones de una lista → pendiente |
| POST   | `/api/songs/remove-items` | Marca ítems para quitar de una lista (cola local, sin subir aún) |
| GET    | `/api/songs/pending-moves` | Reasignaciones de canciones pendientes de subir |
| POST   | `/api/songs/pending-moves/{id}/upload` | Sube a YouTube la reasignación (parcial/reanudable) |
| DELETE | `/api/songs/pending-moves/{id}` | Descarta y revierte la reasignación local |
| GET    | `/api/activity/log` | Log de actividad real en YouTube (últimas N operaciones) |
| GET/POST | `/api/cache/*` | Explorar la cache local |

Los errores se manejan de forma central en `GlobalExceptionMiddleware`: `401` sin sesión Google, `400` petición inválida, `502` fallo de servicio externo, `500` resto.

---

## Persistencia

Todo vive en `YTPlaylistManager.Server/data/` (ya ignorado en `.gitignore`):

- `google-token.json` → tokens OAuth.
- `playlist-cache.json` / `items-cache.json` → cache local de listas y canciones (permite trabajar **offline** sin gastar cuota).
- `pending-uploads.json` → uniones aplicadas en local **pendientes de subir** a YouTube (sobreviven reinicios → la subida es reanudable).
- `pending-song-moves.json` → reasignaciones de canciones pendientes.
- `activity-log.json` → hasta 1000 eventos de actividad real en YouTube (insertar/quitar canción, borrar lista). Persiste entre reinicios; visible en **Historial → Actividad**.
- `merge-reviews.json` / `archived-playlists.json` → logs y registro de uniones.

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

- **Detección de duplicados** en dos niveles: por `videoId` (exacto) y por **título normalizado** (quita paréntesis, "official video", acentos, etc.) — capta "misma canción subida por canales distintos". Dentro de una lista, los grupos de título normalizado aparecen primero, los de mismo video después.
- **Cola de cambios unificada** (`PendingService`): un servicio singleton en el frontend agrupa uniones de listas y reasignaciones de canciones. El panel flotante global refleja el estado en tiempo real desde cualquier pantalla.
- **Local-first**: todas las operaciones de escritura (unir, quitar, reasignar) se aplican en local primero y se sincronizan con YouTube cuando el usuario lo decide. La cuota de YouTube solo se consume en el momento de subir.
- **Listas especiales de YouTube**: los IDs con prefijo `FL` (Favoritos), `WL` (Ver más tarde), `LL` y `RD` no se pueden borrar vía API — se detectan en `IsSpecialPlaylist()` del backend y se omiten del paso de eliminación al subir una unión, evitando que la entrada quede bloqueada permanentemente en la cola.
- **Cuotas API**: YouTube Data API tiene 10.000 unidades/día por defecto. Listar es barato (1 unidad), insertar/borrar items cuesta ~50.
- **UserKey estable**: la clave de usuario para la cache se deriva del `RefreshToken` (no del `AccessToken`, que rota cada hora) — evita fragmentar la cache entre sesiones.
- **Command palette** (`Ctrl+K`): búsqueda local sin cuota. Playlists se filtran client-side; canciones se buscan en cache vía `POST /api/songs/search`. La navegación a una canción usa **router state** (sin query params) para pre-cargar el modo "Por canción" al llegar a `/organizar`.
- **Skeleton loading**: efecto shimmer CSS (`@keyframes shimmer`, `background-size: 600px`) en todos los estados de carga de listas y canciones.
- **BEM en CSS**: todas las clases siguen la convención Bloque__Elemento--Modificador. Cero `style=""` inline en las plantillas HTML — todos los estilos están en `styles.css` con clases semánticas (`card--selected`, `card__check`, `cmd-palette__item--active`, `cross-org__song-row`, etc.).
- **Iconos**: [Font Awesome 6](https://fontawesome.com/) Free vía CDN (sólido + marcas).
- **Diseño "midnight studio"**: fondo tinta + acento lima `#c6f24e`, tipografías Bricolage Grotesque y Hanken Grotesk, design tokens CSS.

---

## Seguridad

Esta herramienta es para **uso local personal**. No despliegues el backend en un servidor público sin antes:
- Encriptar el almacén de tokens.
- Manejar refresh de tokens robusto.
- Limitar CORS a tu propio dominio.
- Usar HTTPS.
