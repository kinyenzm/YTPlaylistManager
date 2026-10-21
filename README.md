# YT Playlist Manager

Herramienta personal full stack (**ASP.NET Core .NET 10** + **Angular 22** — standalone, zoneless, signals, i18n es/en) para gestionar tus playlists de YouTube/YouTube Music: listar, **quitar repetidas**, **unir playlists**, **organizar canciones entre listas** y **recuperar las que quedaron fuera de todas** (local-first, con subida controlada a YouTube), **ordenar canciones con IA** y **respaldar tu registro local** (archivo `.json` o Google Drive).

---

## Requisitos

- **.NET SDK 10** (el backend apunta a `net10.0`; no hay `global.json`, así que sirve cualquier SDK 10.0.x).
- **Node.js** `^22.22.3`, `^24.15.0` o `26.x`, y **npm**: es lo que exige Angular CLI 22.2. `ng` compara la versión mayor, así que con Node 27 o posterior también se cierra con error; las impares 23 y 25 solo muestran un aviso.
- Node.js también hace falta para compilar el backend en local: el `.csproj` referencia `YTPlaylistManager.client.esproj`, así que `dotnet build` / `dotnet run` comprueban `node --version` y ejecutan `npm install` en el cliente si falta `node_modules` o si `package.json` es más reciente que `package-lock.json`.
- **Docker** (opcional), solo para el despliegue en contenedor.

---

## Estructura del proyecto

Arquitectura en capas (estilo MVC), con backend y frontend como proyectos separados en la raíz:

```
YTPlaylistManager/
├── YTPlaylistManager.Server/      # Backend ASP.NET Core (.NET 10)
│   ├── Controllers/              # Auth, Playlists, Songs, Cache, Activity, Backup (respaldo), Operations, Quota (cuota)
│   ├── Domain/
│   │   ├── Entities/             # GoogleTokenData, OperationLogEntry, MergeReviewPlan, PendingUpload, PendingSongMove
│   │   └── Exceptions/           # NotAuthenticatedException (→ 401), AiUnavailableException (→ 503)
│   ├── DTOs/                     # DTOs.cs (records request/response)
│   ├── Services/                 # YouTubeService (IYouTubeService), YouTubeClientFactory, GoogleSessionValidator,
│   │                             #   DuplicateService, SongMoveService, SongSearchService, RecoveryService (canciones huérfanas),
│   │                             #   NvidiaClassifier (IAiClassifier), QuotaTracker (cuota estimada), PlaylistCatalog,
│   │                             #   BackupService + DriveAutoBackupWorker (respaldo .json / Google Drive),
│   │                             #   ActivityBroadcaster (log de actividad en YouTube), UserKeyMigration,
│   │                             #   stores JSON (token, cache de listas/items, pendientes, archivadas, operaciones, cuota)
│   ├── Filters/                  # RequireGoogleSession (401 sin sesión Google)
│   ├── Middleware/               # GlobalExceptionMiddleware
│   ├── Properties/launchSettings.json  # perfiles http (:5080, levanta también Angular vía SpaProxy) y http-api-only (solo la API)
│   ├── appsettings.test.json     # plantilla de configuración: cópiala a appsettings.json
│   ├── data/                     # JSON locales (se crea al ejecutar; en .gitignore)
│   ├── Program.cs
│   └── YTPlaylistManager.Server.csproj
├── YTPlaylistManager.client/      # Frontend Angular 22 (standalone, zoneless, signals)
│   ├── package.json / angular.json  # scripts npm y build (salida en dist/yt-playlist-manager)
│   ├── YTPlaylistManager.client.esproj  # proyecto JS para Visual Studio (referenciado por la solución)
│   ├── launch.json               # depuración en Chrome (localhost:4200)
│   └── src/
│       ├── assets/
│       │   ├── i18n/             # es.json / en.json (ngx-translate, paridad 267 keys)
│       │   └── favicon.svg       # icono SVG (botón play lima sobre fondo oscuro)
│       ├── environments/         # environment.ts / environment.production.ts (apiBaseUrl)
│       ├── proxy.conf.js         # proxy /api → backend en dev
│       ├── styles.css            # estilos globales (design tokens, BEM)
│       ├── index.html / main.ts
│       └── app/
│           ├── app.ts / app.html # shell: navbar, command palette, panel de pendientes
│           ├── app.routes.ts     # rutas ES/EN (lazy) · app.config.ts (zoneless, router, HTTP, ngx-translate, locale es)
│           ├── services/         # api, api-error, auth, drafts, lang, pending, refresh-all (*.service.ts)
│           │                     #   + session.interceptor.ts (401 → sesión caída; 403 → refresca la cuota)
│           ├── models/           # models.ts
│           ├── guards/           # unsaved-drafts.guard.ts (avisa si sales de Organizar con cambios sin guardar)
│           ├── utils/            # youtube.utils.ts
│           ├── components/       # cache-explorer (pantalla Historial: actividad, uniones, respaldo…),
│           │   │                 #   command-palette (Ctrl+K), pending-changes, lang-switcher, list-tools,
│           │   │                 #   assign-modal, song-card, song-history, recover-tab
│           │   └── ui/           # empty-state, modal, skeleton-list (piezas compartidas)
│           └── pages/            # playlists (Mis listas), cross-duplicates (Organizar: Repetidas, Por lista,
│                                 #   Por canción y Recuperar)
├── docs/superpowers/              # specs y planes de diseño (p. ej. organizador unificado)
├── .vscode/                       # launch.json + tasks.json (tarea "dev: full stack": backend y frontend en paralelo)
├── Agents.md                      # perfiles de agentes IA para trabajar en el proyecto
├── best-practices.md              # guía de buenas prácticas de TypeScript/Angular (el código no las cumple todas)
├── Dockerfile                     # build client + backend, sirve el SPA desde wwwroot
├── .dockerignore / .gitignore
├── LICENSE                        # MIT
└── YTPlaylistManager.slnx         # Solución (formato XML): Server + client (.esproj)
```

> **Sin base de datos:** la persistencia es JSON local en `data/` (configurable con `Storage:DataFolder`): token OAuth, cache de listas/items, uniones y reasignaciones pendientes, listas archivadas, cuota estimada y logs. El resto del estado vive en la YouTube Data API. Puedes exportar e importar ese registro como un solo `.json` (*Historial → Respaldo*). Con el scope de Drive, además se respalda solo en tu Google Drive cada vez que el registro cambia, y puedes subirlo o restaurarlo a mano. El respaldo nunca incluye el token, `quota.json` ni `backup-state.json`. Por eso no hay capa `Data/` (EF Core).

---

## 0) Configuración (appsettings)

El repo **no incluye** `appsettings.json` (lleva tus claves, está en `.gitignore`). Copia la plantilla y rellena tus valores:

```bash
cp YTPlaylistManager.Server/appsettings.test.json YTPlaylistManager.Server/appsettings.json
```

Luego edita `appsettings.json`:

- **Obligatorio:** `Google:ClientId` y `Google:ClientSecret` (sección 1).
- **Opcional:** `Ai:NvidiaApiKey` (sección 2) y el scope de Drive en `Google:Scopes` para el respaldo (sección 1).
- `Cors:AllowedOrigins`: orígenes permitidos. El **primero** es también adonde vuelve el navegador tras el login (por defecto `http://localhost:4200`).
- `Storage:DataFolder`: carpeta de datos (por defecto `./data`).
- `YouTube:DailyQuota` (no viene en la plantilla): límite del contador de cuota diaria (por defecto `10000`).

Las claves `Google:key`, `Google:QuotaPooling` y `Google:ApiKeys` de la plantilla ya no se usan: puedes dejarlas o borrarlas. Tus claves quedan locales, no se suben al repo.

---

## 1) Credenciales Google (obligatorio)

1. Entra a https://console.cloud.google.com/ y crea (o reutiliza) un proyecto.
2. Habilita la **YouTube Data API v3**. Si vas a usar el respaldo en Drive, habilita también la **Google Drive API**.
3. Configura la **OAuth consent screen** (External, modo Testing es suficiente para uso personal). Agrega tu propio correo como **Test user**.
4. Crea **OAuth client ID** → tipo *Web application*.
   - Authorized redirect URI: `http://localhost:5080/api/auth/callback` (es el valor de `Google:RedirectUri`).
5. Copia `Client ID` y `Client Secret` y pégalos en `YTPlaylistManager.Server/appsettings.json` bajo `Google`.

**Scopes usados** (en `Google:Scopes`):
- `https://www.googleapis.com/auth/youtube` (escribir: borrar items, crear playlists, agregar items)
- `https://www.googleapis.com/auth/youtube.readonly` (leer)
- `https://www.googleapis.com/auth/drive.appdata` (**opcional**, respaldo en Google Drive). No viene en la plantilla: agrégalo a mano a `Google:Scopes` si quieres respaldar en Drive. Al volver a conectar, la app pide el permiso nuevo automáticamente. Sin este scope, el respaldo en Drive (manual y automático) queda desactivado; exportar e importar el archivo `.json` sigue funcionando.

---

## 2) Proveedor de IA para ordenar canciones (NVIDIA)

**Para qué se usa:** el botón **"Ordenar con IA"** (en *Organizar → Por lista*) agrupa las canciones por **género / ánimo / década**. Lo resuelve un LLM hospedado en **NVIDIA NIM** (`build.nvidia.com`, tier gratis). Es opcional: si **no** configuras la API key, o si ningún modelo responde, el botón muestra *"Revisa la configuración de IA"* (el backend responde `503`). El resto de la app funciona igual. No hay clasificación de respaldo sin IA.

**Cómo obtener la API key (gratis):**
1. Entra a https://build.nvidia.com/ y crea una cuenta (NVIDIA Developer).
2. Elige un modelo (ej. `meta/llama-3.3-70b-instruct`) → **Get API Key** → copia el token `nvapi-...`.
3. Pégalo en `appsettings.json` bajo `Ai`:

```json
"Ai": {
  "Provider": "nvidia",
  "NvidiaApiKey": "nvapi-XXXXXXXXXXXXXXXX",
  "NvidiaModel": "meta/llama-3.3-70b-instruct",
  "NvidiaBaseUrl": "https://integrate.api.nvidia.com/v1/",
  "NvidiaFallbackModels": []
}
```

Si el modelo principal falla, se prueban en orden los modelos de `NvidiaFallbackModels`. Esta lista es opcional: si la dejas vacía o no la pones, se reintenta con `meta/llama-3.3-70b-instruct`. Cada intento tiene un timeout de 12 s. `Provider` solo implementa `nvidia`: cualquier otro valor usa el mismo clasificador.

La API de NVIDIA es **compatible con OpenAI Chat Completions**, así que el mismo `NvidiaClassifier.cs` funciona con cualquier endpoint OpenAI-compatible (Together.ai, Groq, vLLM local, LM Studio, Ollama con `/v1`, etc.): solo cambia `NvidiaBaseUrl` y el modelo. Aunque el endpoint no pida clave, pon en `NvidiaApiKey` algún valor que no esté vacío ni empiece por `TU_`; si no, la IA se considera sin configurar.

---

## 3) Ejecutar el backend

```bash
cd YTPlaylistManager.Server
dotnet restore
dotnet run --launch-profile http
```

El perfil `http` levanta la API en `http://localhost:5080` y, con **SpaProxy**, también arranca Angular (`npm start` en `YTPlaylistManager.client`). Con este único comando ya tienes la app completa en `http://localhost:4200` (`http://localhost:5080` redirige ahí en cuanto Angular está listo).

Si prefieres arrancar el frontend en otra terminal (sección 4), usa `dotnet run --launch-profile http-api-only`: solo levanta la API. `dotnet run` no abre el navegador (la opción `launchBrowser` del perfil solo la usan Visual Studio y `dotnet watch`), así que abre tú la UI **Scalar** en `http://localhost:5080/scalar/v1`.

En Development tienes **Scalar** en `http://localhost:5080/scalar/v1` y el documento OpenAPI nativo de .NET 10 en `http://localhost:5080/openapi/v1.json`. Fuera de Development no se publican.

---

## 4) Ejecutar el frontend

> Solo necesitas este paso si arrancaste el backend con `--launch-profile http-api-only`. Con el perfil `http`, SpaProxy ya lanza `npm start` por ti.

```bash
cd YTPlaylistManager.client
npm install
npm start
```

Luego abre `http://localhost:4200` en el navegador (`npm start` no lo abre solo). El `proxy.conf.js` redirige `/api` → `http://localhost:5080`, así no hace falta tocar CORS en dev.

### Desde el IDE

- **Visual Studio:** abre `YTPlaylistManager.slnx`, deja **YTPlaylistManager.Server** (perfil `http`) como único proyecto de inicio y pulsa F5. SpaProxy arranca Angular y el navegador termina en `http://localhost:4200`. No hace falta iniciar también el `.esproj`.
- **VS Code:** `Ctrl+Shift+B` ejecuta la tarea `dev: full stack` (backend con el perfil `http` + `npm start` en paralelo). Para depurar el backend usa la configuración **Backend (.NET)**: arranca antes `ng serve`, pero no compila, así que ejecuta `dotnet build` en `YTPlaylistManager.Server` la primera vez y después de cada cambio.

---

## 5) Flujo

1. **Conectar con Google** → consent → vuelve con sesión activa. Verás tus listas en **Mis listas**, con skeleton loading mientras cargan.
2. **Quitar repetidas** (en *Organizar → Por lista*, o tocando el título de una lista en *Mis listas*): *Buscar repetidas* relee la lista desde YouTube y agrupa las copias; en cada grupo eliges *Quitar* o *Dejar esta*. Los duplicados se priorizan: **mismo título primero**, mismo video después. *Eliminar repetidas* las quita todas de una según el criterio elegido (*Mismo video (exacto)* o *Mismo título (aunque sea otro video)*) y conserva la primera copia de cada grupo. Los cambios van a la **cola global de subidas**.
3. **Unir listas** (en *Mis listas*), modelo **local-first**:
   - Haz clic en 2+ listas (toda la tarjeta responde al clic, y con teclado se seleccionan con el botón de su esquina; las archivadas o en cola no se pueden elegir). En *Juntar todo en:* eliges la lista destino (por defecto la más grande, porque necesita menos cambios) → **Revisar y unir** → vista previa con las canciones que se agregan y de qué lista vienen → **Aplicar unión (N)**. Si ya están todas en la destino, el botón es **Unir igual (borrar las repetidas)** y las listas origen quedan en cola para borrarse.
   - La unión se aplica **en local** (0 cuota) y queda **pendiente de subir**; aparece la burbuja flotante de cambios pendientes. Las listas origen quedan marcadas como *en cola*.
   - **Subir a YouTube** (desde el panel de cambios pendientes, con confirmación): inserta las canciones en la lista destino y **borra las listas origen** de tu cuenta. Es **parcial y reanudable**: si se agota la cuota diaria, la subida se pausa; cuando se renueve la cuota, toca *Subir* otra vez y sigue desde donde quedó (no vuelve a subir lo ya subido). Mientras sube, el panel muestra una barra de progreso indeterminada (*Subiendo a YouTube…*); al terminar, un resumen con las canciones subidas, las listas borradas y, si se cortó, cuántas faltan.
   - **Listas especiales de YouTube** (Favoritos `FL`, Ver más tarde `WL`, Me gusta `LL`, Mezcla `RD`): la API no permite escribirlas. Como origen, se omite su borrado al subir, para que la unión no quede bloqueada para siempre. Como destino no se aceptan (ni al unir ni al recuperar), y un pendiente que ya apunte a una de ellas aparece **bloqueado** en el panel, sin botón de subir: solo puedes descartarlo.
   - **Descartar** revierte la unión local sin tocar YouTube.
4. **Organizar canciones** (menú *Organizar*, ruta `/organizar`), también **local-first**. Cuatro modos:
   - **Repetidas**: las canciones (mismo video) que están en 2+ listas, primero las que están en más listas. *Buscar* usa la cache (solo lee de YouTube las listas que aún no están cargadas); *Volver a revisar* relee todas tus listas desde YouTube (gasta cuota).
   - **Por lista**: eliges una lista (o entras desde el título de una tarjeta en *Mis listas*, ruta `/organizar/lista/:id`) y ves todas sus canciones: primero las que tienen copias dentro de la misma lista (*×N en esta lista*), luego las que están en más listas. En cada canción, la ✕ de una lista en *Aparece en:* la marca para quitar (↺ la restaura) y *Asignar a listas* abre el selector. Arriba tienes las herramientas de la lista: *Buscar repetidas*, *Eliminar repetidas* y *Ordenar con IA*.
   - **Por canción**: escribes un nombre o un ID de video (busca mientras escribes) y eliges dónde buscar: *Todas*, *Activas* o *Archivadas* (listas que ya quitaste de YouTube). *Archivadas* solo encuentra las listas archivadas que siguen en la cache: las que la app borró de YouTube al subir una unión pierden sus canciones en la cache. Al tocar una tarjeta se abre el selector de listas.
   - **Recuperar**: canciones que la app conoce (listas borradas, historial) pero que hoy no están en ninguna de tus listas. Puedes filtrarlas por la lista donde se vieron por última vez, seleccionarlas y encolarlas hacia una lista existente o una **lista nueva** (se crea privada en YouTube al encolar, 50 unidades). Cada una indica si *No existe en tus playlists actuales* o si *Existe con otro video*, en cuyo caso recuperarla duplicaría la música.
   - En *Repetidas* y *Por lista*, *Asignar a listas* (en *Por canción*, tocar la tarjeta) abre un selector con **todas tus listas** (marcadas donde está ahora) y eliges dónde debe quedar: **en varias o en una sola**. Se agrega a las nuevas y se quita de las desmarcadas. *Aplicar (queda pendiente)* solo guarda un **borrador local**: la barra inferior muestra *N canción(es) con cambios sin guardar* con **Guardar cambios** (los manda a la cola de pendientes) o **Descartar**, y cada tarjeta tiene *Deshacer*. Si sales de *Organizar* con borradores, la app pregunta si quieres guardarlos: *Aceptar* los manda a la cola y sale; *Cancelar* te deja en la pantalla (para salir sin guardar, descártalos primero). Al cerrar la pestaña, el navegador te avisa. Los videos privados o eliminados no se pueden reasignar.
5. **Búsqueda rápida — Ctrl+K** (⌘K en Mac): abre el command palette desde cualquier pantalla (también con el botón de búsqueda de la barra superior). Al abrir muestra tus primeras listas. Busca en local sin cuota:
   - **Playlists** por nombre o descripción (filtro client-side instantáneo). Elegir una abre *Organizar → Por lista* con esa lista.
   - **Canciones** por nombre o videoId parcial (un texto de 8+ caracteres hecho solo de letras sin tilde, números, `-` y `_` se busca como ID, así que "Metallica" también se busca como ID), desde 2 caracteres y solo en tus listas activas (búsqueda en cache vía API). Elegir una canción abre el modo **Por canción** ya buscado.
   - Navegar con ↑↓, confirmar con ↵, cerrar con Esc.
6. **Cambios pendientes — panel global**: una burbuja en la esquina inferior izquierda (un ícono con el número de cambios; *N sin subir · ~Qu* aparece al pasar el mouse o al enfocarla) agrupa *todas* las subidas pendientes de cualquier pantalla: uniones y recuperaciones (*Uniones de listas*), y reasignaciones y copias repetidas quitadas (*Cambios de canciones*). Muestra el costo estimado en unidades, y puedes subir o descartar de forma individual o en bloque (*Subir todo*, *Descartar todo*), siempre con confirmación. Si la lista destino de una unión o recuperación ya no existe o es una lista automática de YouTube, el cambio queda marcado y solo se puede descartar; en un cambio de canción, las listas que ya no existen se omiten. El log completo de actividad real en YouTube queda en **Historial → Actividad**.
7. **Ordenar con IA** (en *Organizar → Por lista*): eliges *Ordenar por género / ánimo / década* y tocas **Ordenar con IA**. Las canciones se muestran agrupadas por categoría (se piden como máximo 10) dentro de la app; no cambia el orden de la lista en YouTube. Sin API key de NVIDIA, o si ningún modelo responde, verás *Revisa la configuración de IA*.
8. **Idioma**: selector **ES | EN** arriba. La primera vez usa inglés si tu navegador está en inglés y español en cualquier otro caso; después recuerda tu elección. Las fechas, la cuota de la barra superior y las cifras de *Historial* siguen el idioma elegido; el resto de los números se muestra sin formato. Las rutas existen en ambos idiomas (`/organizar` ↔ `/organize`, `/organizar/lista/:id` ↔ `/organize/list/:id`, `/datos` ↔ `/data`), y al cambiar de idioma la URL actual se traduce sola.
9. **Historial** (menú *Historial*, ruta `/datos`): registro local que guarda el servidor en su carpeta `data/` (no el navegador, así que borrar los datos del navegador no lo afecta). Pestañas: *Resumen* (totales de listas, canciones, uniones y archivadas), *Listas que quité de YouTube* (y a qué lista fueron sus canciones), *Uniones* (solo el historial de versiones anteriores; las uniones nuevas aparecen en *Listas que quité de YouTube* y en *Actividad*), *Actividad* (las últimas 1000 acciones hechas de verdad en YouTube; crear una lista nueva desde *Recuperar* no se registra), *Historial por canción* (en qué listas está hoy una canción según la cache, 0 cuota; no guarda su recorrido) y *Respaldo*. Puedes cambiar de pestaña con ←/→, Inicio y Fin.
10. **Respaldo** (*Historial → Respaldo*, o *Exportar* en la parte superior de *Historial*, que baja el mismo `.json`): *Descargar respaldo* baja un `.json` con el historial, las listas archivadas, las canciones recuperables y los pendientes (nunca tus credenciales de Google). *Restaurar desde archivo…* reemplaza el registro actual y antes guarda una copia en `data/backups/`. Con **Google Drive** se guarda un solo archivo en la carpeta oculta de la app en tu Drive, que se sube solo cuando el registro cambia; también puedes *Respaldar en Drive ahora* o *Restaurar desde Drive*. Para activarlo, agrega `https://www.googleapis.com/auth/drive.appdata` a `Google:Scopes`, habilita la **Google Drive API** en tu proyecto y usa *Reconectar con Google*.
11. **Cuota y avisos**: con sesión, la barra superior muestra la cuota de YouTube estimada que te queda hoy (`restante/límite`; solo cuenta lo que gasta esta app); se actualiza cada 10 s y se pone roja por debajo de 500 unidades. Si tu sesión de Google expira a mitad de uso, aparece un aviso con *Conectar con Google*. Mientras corre *Actualizar todo*, verás *Actualizando todas las listas…* en cualquier pantalla.

Casi todo funciona **offline** desde la cache (0 cuota). Usan la API de YouTube: *Actualizar todo* (pide confirmación y sigue corriendo aunque cambies de pantalla), *Subir a YouTube*, *Buscar repetidas*, *Volver a revisar* en *Repetidas*, crear una *Lista nueva* en *Recuperar*, conectar con Google y la primera carga de listas o canciones que aún no están en la cache. *Por lista*, la búsqueda y el command palette solo ven las canciones que ya están en la cache: si una lista aparece vacía, usa *Actualizar todo*.

---

## Endpoints REST principales

| Método | Ruta | Descripción |
|--------|------|-------------|
| GET    | `/api/auth/login` | Inicia OAuth (`?consent=true` fuerza la pantalla de permisos, p. ej. para agregar el scope de Drive) |
| GET    | `/api/auth/callback` | Callback OAuth (uso interno) |
| GET    | `/api/auth/status` | ¿Hay sesión activa? (incluye `driveBackupEnabled`) |
| POST   | `/api/auth/logout` | Cierra la sesión local: invalida el access token y conserva el refresh token (no revoca el permiso en Google) |
| GET    | `/api/playlists` | Lista tus listas (cache; `?refresh=true` para releer, `?includeArchived=true` incluye las archivadas) |
| GET    | `/api/playlists/{id}/items` | Canciones de una lista (`?cacheOnly=true`: solo cache, 0 cuota) |
| GET    | `/api/playlists/{id}/duplicates` | Repetidas dentro de una lista (siempre desde YouTube) |
| GET    | `/api/playlists/cross-duplicates` | Repetidas entre listas (`?refresh=true` relee desde YouTube) |
| POST   | `/api/playlists/remove-duplicates` | Encola quitar las repetidas de una lista (conserva la primera de cada grupo) → pendiente. La lista debe estar en cache |
| POST   | `/api/playlists/merge` | Une en local → deja pendiente de subir |
| POST   | `/api/playlists/merge/preview` | Vista previa de la unión (0 cuota) |
| GET    | `/api/playlists/pending-uploads` | Uniones y recuperaciones pendientes de subir |
| POST   | `/api/playlists/pending-uploads/{id}/upload` | Sube a YouTube. Borra las listas origen (si no son especiales) solo cuando todas las canciones subieron sin fallos; `?limit=N` sube como máximo N canciones en esa llamada |
| DELETE | `/api/playlists/pending-uploads/{id}` | Descarta una unión (y la revierte en local) o una recuperación. Si la recuperación ya creó una lista nueva, esa lista sigue en YouTube |
| POST   | `/api/playlists/refresh-all` | Relee todas las listas desde YouTube |
| POST   | `/api/playlists/{id}/classify` | Ordena con IA |
| POST   | `/api/songs/search` | Busca canciones por nombre o videoId (en cache) |
| GET    | `/api/songs/{videoId}/locations` | Listas donde está la canción (cache, 0 cuota) |
| POST   | `/api/songs/locations` | Ubicaciones de varias canciones a la vez (videoId → listas) |
| GET    | `/api/songs/duplicate-counts` | Por lista, cuántas canciones están también en otra lista (cache, 0 cuota) |
| POST   | `/api/songs/assign` | Asigna la canción a un set de listas (agrega/quita) → pendiente |
| POST   | `/api/songs/remove-items` | Marca ítems para quitar de una lista → pendiente |
| GET    | `/api/songs/recoverable` | Canciones que quedaron fuera de todas tus listas (cache, 0 cuota) |
| POST   | `/api/songs/recover` | Encola recuperarlas en una lista existente o nueva → pendiente en `pending-uploads` (la lista nueva se crea privada en YouTube al momento) |
| GET    | `/api/songs/pending-moves` | Reasignaciones de canciones pendientes de subir |
| POST   | `/api/songs/pending-moves/{id}/upload` | Sube a YouTube la reasignación (parcial/reanudable) |
| POST   | `/api/songs/pending-moves/upload-all` | Sube todas las reasignaciones pendientes |
| DELETE | `/api/songs/pending-moves/{id}` | Descarta y revierte la reasignación local |
| DELETE | `/api/songs/pending-moves` | Descarta y revierte todas las reasignaciones pendientes |
| GET    | `/api/activity/log` | Log de actividad real en YouTube (`?take=N`, por defecto 200) |
| GET    | `/api/cache/status` | Estadísticas de la cache local (listas, canciones, uniones, archivadas) |
| GET    | `/api/cache/song/{videoId}/history` | Historial de movimientos de una canción |
| GET    | `/api/cache/playlists-archived` | Listas archivadas (consolidadas en otra) |
| GET    | `/api/cache/merge-reviews` | Historial heredado de uniones (solo lectura) |
| GET    | `/api/backup/export` | Descarga el registro local como un `.json` (sin credenciales) |
| POST   | `/api/backup/import` | Restaura un respaldo `.json` (multipart, campo `file`; antes guarda una copia en `data/backups/`) |
| GET    | `/api/backup/status` | Estado del respaldo en Drive (permiso, última subida, archivo remoto) |
| POST   | `/api/backup/drive/upload` | Sube el respaldo a la carpeta oculta de la app en Google Drive |
| POST   | `/api/backup/drive/restore` | Restaura el respaldo desde Google Drive |
| GET    | `/api/quota` | Cuota de YouTube estimada hoy (usada, límite, restante; se reinicia a medianoche hora Pacífico) |
| GET    | `/api/operations` | Log de operaciones locales de la app (`operations.json`) |

Los errores se manejan de forma central en `GlobalExceptionMiddleware` (y en el filtro `RequireGoogleSession` cuando falta la sesión) y responden `{ message, statusCode }`:
- `401`: no hay sesión Google, o la sesión expiró o se revocó.
- `400`: petición inválida (`ArgumentException` en el servicio).
- Errores de la API de YouTube: el mismo código que devuelva Google (`403` cuota agotada, `404` lista inexistente, etc.).
- `503`: IA no disponible u operación cancelada.
- `502`: fallo de red con un servicio externo.
- `500`: el resto.

Excepciones a ese formato:
- Un cuerpo de petición que no se puede leer (JSON mal formado o de otro tipo) lo rechaza ASP.NET antes de llegar al controlador: `400` con *ProblemDetails*.
- `POST /api/songs/search` (`400`) y `GET /api/cache/song/{videoId}/history` (`400` / `404`) responden solo `{ message }`.
- `POST /api/backup/import` sin archivo responde `400` `{ code, message }`.
- En `/api/backup/drive/*`, solo los errores de la API de Google (`GoogleApiException`) responden `{ code, message }`: `409` falta el permiso de Drive, `503` API de Drive deshabilitada y `502` cualquier otro. El resto (por ejemplo, todavía no hay respaldo en Drive o el respaldo no es válido) pasa por el middleware como `400`.

---

## Persistencia

Todo vive en `YTPlaylistManager.Server/data/` (configurable con `Storage:DataFolder`; ya ignorado en `.gitignore`). La cache y los pendientes llevan una clave de usuario (`UserKey`), pero la app es de una sola cuenta: al conectar, todos los datos se reasignan a la cuenta actual, y `playlist-cache.json` guarda una sola cuenta. No hay subcarpetas por usuario.

- `google-token.json` → tokens OAuth (en texto plano; nunca entra en los respaldos).
- `playlist-cache.json` / `items-cache.json` → cache local de listas y canciones (permite trabajar **offline** sin gastar cuota).
- `pending-uploads.json` → uniones (ya aplicadas en la cache local) y recuperaciones **pendientes de subir** a YouTube (sobreviven reinicios → la subida es reanudable).
- `pending-song-moves.json` → reasignaciones y remociones de canciones pendientes.
- `activity-log.json` → hasta 1000 eventos de actividad real en YouTube (insertar/quitar canción, borrar lista). Persiste entre reinicios; visible en **Historial → Actividad**.
- `archived-playlists.json` → listas origen borradas de YouTube al subir una unión, con la lista en la que se consolidaron.
- `merge-reviews.json` → historial de uniones del flujo anterior (solo lectura; ya no se escribe).
- `operations.json` → log de operaciones de la app (`GET /api/operations`).
- `playlist-touch.json` → última modificación local de cada lista.
- `quota.json` → contador estimado de cuota de YouTube del día (propio de esta máquina; no se respalda).
- `backup-state.json` → estado del respaldo automático en Drive (propio de esta máquina; no se respalda).
- `backups/pre-import-<fecha>/` → copia del estado anterior que se guarda antes de restaurar un respaldo.

---

## Despliegue con Docker

El `Dockerfile` compila el frontend (Node 24), publica el backend (.NET SDK 10) y sirve el Angular ya compilado desde `wwwroot` del backend (un solo contenedor, mismo origen → no hace falta proxy en prod). La etapa del backend copia solo `YTPlaylistManager.Server`: así `dotnet publish` no intenta compilar el proyecto Angular, que necesitaría Node.js.

```bash
docker build -t ytplaylistmanager .
docker run -p 8080:8080 -v ytpm-data:/app/data ytplaylistmanager
```

Dentro del contenedor los datos (token OAuth, cache, pendientes, logs) viven en `/app/data`. Sin el volumen `-v ytpm-data:/app/data` se pierden al borrar el contenedor.

**Configuración en el contenedor:** el `.dockerignore` no excluye `appsettings.json`, así que si existe al hacer `docker build` queda **dentro de la imagen** con tus claves (no la subas a un registro público). También puedes montarlo al arrancar. El archivo montado reemplaza por completo al de la imagen (no se combinan), así que debe traer todas las claves:

```bash
docker run -p 8080:8080 \
  -v ytpm-data:/app/data \
  -v "$(pwd)/YTPlaylistManager.Server/appsettings.json:/app/appsettings.json:ro" \
  ytplaylistmanager
```

O sobrescribe claves sueltas con variables de entorno, usando `__` en lugar de `:` (por ejemplo `-e Google__ClientSecret=...`). El contenedor corre en `Production`, así que Scalar y `/openapi/v1.json` no están disponibles.

La app queda en `http://localhost:8080`. Para que el login funcione en el contenedor:

- Agrega `http://localhost:8080/api/auth/callback` (o `https://<tu-host>/api/auth/callback`) como **Authorized redirect URI** en Google y pon el mismo valor en `Google:RedirectUri`.
- Pon la URL de la app como **primer** valor de `Cors:AllowedOrigins` (por ejemplo `http://localhost:8080`): tras el callback de Google, el backend redirige ahí (`/?auth=ok`). Con el valor por defecto te mandaría a `http://localhost:4200`.
- Con variables de entorno: `-e Google__RedirectUri=http://localhost:8080/api/auth/callback -e Cors__AllowedOrigins__0=http://localhost:8080`.
- El contenedor solo sirve HTTP. Fuera de `localhost`, Google exige HTTPS, así que pon delante un proxy inverso con TLS.

---

## Notas de diseño

- **Detección de duplicados** en dos niveles: por `videoId` (exacto) y por **título normalizado**, que quita lo que va entre paréntesis o corchetes, acentos, signos y palabras de ruido como "official", "video", "lyrics", "feat" o "remastered". Así capta "misma canción subida por canales distintos". Los videos privados o eliminados se ignoran. Dentro de una lista, los grupos de título normalizado aparecen primero y los de mismo video después; *Eliminar repetidas* conserva la primera copia (menor posición) de cada grupo. Entre listas (modo *Repetidas*) solo se compara por `videoId`.
- **Cola de cambios unificada** (`PendingService`): un servicio singleton en el frontend agrupa uniones de listas, recuperaciones y cambios de canciones (reasignaciones y copias repetidas quitadas). El panel flotante global se actualiza tras cada acción y cada 30 s, desde cualquier pantalla.
- **Local-first**: unir, quitar y reasignar se aplican primero en la cache local y se sincronizan con YouTube cuando decides subir; recuperar solo encola (la lista destino no cambia en local hasta que subes y se vuelve a leer). En *Organizar*, además, los cambios pasan antes por un borrador que confirmas con *Guardar cambios*. La única escritura inmediata es crear una *Lista nueva* desde *Recuperar* (se crea privada en YouTube al encolar, 50 unidades). Fuera de eso, la cuota se gasta al subir y en las lecturas desde YouTube (*Actualizar todo*, *Buscar repetidas*, *Volver a revisar*).
- **Listas especiales de YouTube**: los IDs con prefijo `FL` (Favoritos), `WL` (Ver más tarde), `LL` (Me gusta) y `RD` (Mezcla) no admiten escritura vía API. `PlaylistCatalog.IsSpecialPlaylist()` las detecta: como origen de una unión se omiten del paso de borrado (la entrada no queda bloqueada en la cola), como destino se rechazan al unir o recuperar, y un pendiente que ya apunte a una de ellas se marca `TargetLocked` y solo se puede descartar.
- **Cuotas API**: YouTube Data API da 10.000 unidades/día por defecto. `YouTube:DailyQuota` solo cambia el límite del contador propio de la app, no la cuota real. Listar cuesta 1 unidad por página de 50 (listas o canciones); insertar o borrar una canción, borrar una lista o crear una lista cuesta 50. La app lleva su propio contador estimado en `data/quota.json` (solo cuenta lo que gasta esta app) y lo reinicia a medianoche, hora del Pacífico, como YouTube. Si YouTube responde *quotaExceeded*, lo marca como agotado hasta el reinicio.
- **UserKey estable**: la clave de usuario para la cache es un hash del **ID de tu canal de YouTube** (`AccountId`), que se obtiene al conectar y no cambia entre logins. Si no se puede obtener, se usa el `RefreshToken` como respaldo. Al resolver el canal, los datos guardados con la clave anterior se migran a la nueva, así la cache no se fragmenta.
- **Command palette** (`Ctrl+K`): búsqueda local sin cuota. Las playlists se filtran client-side y las canciones se buscan en cache vía `POST /api/songs/search`. Elegir una canción navega a `/organizar?q=<videoId>`: el organizador abre el modo "Por canción" con ese ID, busca y quita el parámetro (`replaceUrl`) para que *Atrás* no lo repita. Elegir una playlist navega a `/organizar/lista/:id`.
- **Skeleton loading**: efecto shimmer CSS (`@keyframes shimmer`) en los estados de carga de listas y canciones.
- **CSS**: todos los estilos viven en un único `styles.css` global (sin estilos por componente), con clases BEM (`card--selected`, `card__toggle`, `cmd-palette__item--active`, `cross-org__song-row`, etc.) junto a utilidades compartidas (`row`, `muted`, `tag`, `secondary`, `danger`). Casi no hay `style=""` inline: quedan algunos en `command-palette.html` y `cache-explorer.html`.
- **Iconos**: [Font Awesome 6](https://fontawesome.com/) Free (6.5.2) vía CDN de cdnjs (`all.min.css`: sólido, regular y marcas).
- **Diseño "midnight studio"**: fondo tinta + acento lima `#c6f24e`, tipografías Bricolage Grotesque y Hanken Grotesk, design tokens CSS.
- **Responsive**: en tablet el header queda en una fila y en teléfono en dos, con el buscador como ícono; los modos de *Organizar* se deslizan en su propia fila si no caben.
- **Accesibilidad**: enlace *Saltar al contenido*. Los diálogos (incluido el command palette) se cierran con Esc, mantienen el foco dentro (Tab no sale al fondo) y lo devuelven al cerrar. Las tarjetas seleccionables tienen un botón equivalente para teclado y lector (`aria-pressed`), y las pestañas de *Historial* se recorren con flechas, Inicio y Fin. Con `prefers-reduced-motion` se apagan las animaciones, incluido el shimmer.

---

## Seguridad

Esta herramienta es para **uso local personal** y de **una sola cuenta**. El backend no tiene login propio: cualquiera que alcance el puerto actúa con la cuenta de Google guardada en `data/google-token.json` (además, `/api/activity`, `/api/operations` y `/api/quota` ni siquiera exigen sesión). No despliegues el backend en un servidor público sin antes:
- Encriptar el almacén de tokens (`google-token.json` guarda el access token y el refresh token en texto plano).
- Agregar autenticación propia delante de la API.
- Ajustar `Cors:AllowedOrigins` (hoy solo `http://localhost:4200`) y `AllowedHosts` (hoy `*`) a tu propio dominio.
- Usar HTTPS (el backend no lo configura).

Ya resuelto: el refresh del token es automático, los respaldos nunca incluyen `google-token.json` y Scalar/OpenAPI solo se publican en `Development`.

**Cerrar sesión** no revoca el permiso en Google y conserva el refresh token en disco. Para revocarlo del todo, quita la app en https://myaccount.google.com/permissions y borra `data/google-token.json`.
