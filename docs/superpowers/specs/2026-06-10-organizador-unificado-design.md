# Diseño: Organizador unificado + limpieza de videos no disponibles

**Fecha:** 2026-06-10
**Estado:** Aprobado por Steven

## Contexto

El sitio tiene dos pantallas que se solapan: `/organizar` (CrossDuplicates, 3 modos) y `/buscar`
(SongSearch, tabla con filtrado en vivo). El modo "Por canción" de Organizar duplica la búsqueda
con peor UX (requiere Enter). Además:

- Los videos privados/eliminados ("Private video" / "Deleted video") aparecen como duplicados
  falsos en todas las detecciones (cross-playlist, detalle de playlist por título normalizado,
  búsquedas).
- El selector de ámbito (`searchScope`) llega al backend pero `SongSearchService.SearchCombined`
  lo ignora — el filtro "archivadas" no hace nada.
- El botón Acciones (📊) de /buscar es un `console.log` muerto.
- El título de `playlist/{id}` solo se llena tras "Buscar duplicados" (sale de
  `duplicates()?.playlistTitle`); al navegar directo queda el título genérico.
- Las tarjetas de playlists no muestran fecha de modificación (YouTube API no la expone).

## Decisiones de diseño

### 1. Fusión `/buscar` → `/organizar`

- Rutas `buscar` y `search` redirigen a `organizar` / `organize` (modo "Por canción" por defecto).
- Componente `SongSearch` se elimina; ítem "Buscar" sale del menú de navegación.
- El modo "Por canción" de Organizar absorbe lo bueno de /buscar:
  - Dos filtros, en este orden: **Nombre** (primero) e **ID de video** (después).
  - **Filtrado en vivo** con debounce ~400 ms, sin necesidad de Enter (Enter sigue funcionando
    como atajo de búsqueda inmediata).
  - Selector de ámbito: Todas / Activas / Archivadas.

### 2. Ámbito de búsqueda funcional (backend)

- `SearchCombined(videoIdPartial, songNameFuzzy, searchScope)` aplica el scope de verdad:
  - `all`: todo el caché (comportamiento actual).
  - `active`: excluye playlists archivadas.
  - `archived`: solo playlists archivadas.
- Fuente de verdad de archivado: `ArchivedPlaylistsStore` (mismo origen que usa
  `GetArchivedPlaylistsAsync`).

### 3. Exclusión de videos privados/eliminados

- Helper central compartido `IsUnavailable(item)`: título igual a `"Private video"` o
  `"Deleted video"` (case-insensitive). Estos títulos los devuelve la YouTube API en inglés
  siempre, independiente del idioma de la cuenta.
- Se aplica en:
  - `FindCrossDuplicatesAsync` (Organizar → Repetidas): items no disponibles nunca forman grupo.
  - `FindDuplicatesAsync` (detalle de playlist): excluidos de ambas categorías — "mismo video"
    y "mismo título normalizado". Hoy todos los "Deleted video" se agrupan como misma canción.
  - `SongSearchService` (búsquedas por nombre/ID): excluidos de resultados.
- En el modo "Por lista" del organizador NO se ocultan: se muestran con tag "No disponible"
  pero en SOLO LECTURA — sin badges editables ni "Asignar a listas" (YouTube no permite
  gestionar videos sin acceso). Habilitar su edición queda como configuración futura.

### 4. Badges de playlists editables en tarjetas (3 modos del organizador)

- Cada badge de playlist en la tarjeta de una canción es editable:
  - Hover sobre badge → aparece **×**.
  - Click en × → badge queda tachado (eliminación preparada, solo estado local); icono **↺**
    sobre el badge tachado lo restaura.
- Con ≥1 cambio preparado, la tarjeta muestra botón **"Guardar cambios (n)"** + opción deshacer
  todo. Guardar invoca `assignSong` con `desiredPlaylistIds` = (listas actuales − quitadas);
  entra al flujo staged existente (panel global de pendientes). Sin abrir modal.
- **"+ Asignar"** (botón existente) abre el modal solo para agregar a listas nuevas.
- Modal de asignación: **se elimina el control "una / varias"** (`singleMode`, `setSingle`,
  segmented control). Siempre multi-selección.

### 5. Fecha de modificación en tarjetas de playlists (vista principal)

- Backend deriva `lastModifiedUtc` por playlist desde el log de actividad propio
  (`activity-log.json`: agregados/quitados/merges). YouTube no expone esta fecha.
- `PlaylistDto` gana campo opcional `lastModifiedUtc`.
- Tarjeta muestra fecha relativa ("Modificada hace 2 días", i18n es/en).
- La cuadrícula ordena: con actividad → recientes primero; sin actividad → después, en orden
  alfabético actual. Sin fecha no se muestra etiqueta.

### 6. Título de `playlist/{id}` siempre cargado

- Al entrar al detalle, el nombre se resuelve desde `listPlaylists()` (sirve caché local,
  0 cuota) buscando por id; señal propia `playlistTitle`.
- El `<h3>` usa esa señal; el reporte de duplicados deja de ser la fuente del título.

### 7. Contador de repetidas por playlist en "Por lista"

- Con la data ya cacheada (items por playlist), el backend expone un mapa
  `playlistId → duplicateCount` (canciones de esa lista que aparecen en ≥2 playlists).
  Cálculo 100% sobre caché local, 0 cuota. Endpoint nuevo cache-only
  (p. ej. `GET /api/songs/duplicate-counts`).
- El selector de "Por lista" muestra el número junto a cada lista:
  `Título (120) — 5 repetidas`. Limitación: `<option>` nativo no permite color rojo;
  el número va en el texto de la opción, y junto al selector se muestra un badge rojo
  con las repetidas de la lista seleccionada.

### 8. Título del documento (pestaña del navegador) en detalle

- Al entrar a `playlist/{id}`, además del `<h3>`, se setea el título del documento vía
  `Title` de Angular: `"{nombre de la lista} — {nombre de la app}"`.
- Al salir de la página se restaura el título base de la app.

### 9. Timeout y feedback del botón "Ordenar por IA"

- `NvidiaClassifier` hoy no configura timeout → HttpClient default 100 s colgado sin
  conectividad. Se configura timeout corto (~12 s) en el cliente HTTP nombrado.
- Si falla por timeout/red/configuración, el backend responde error claro y el botón
  muestra tooltip/mensaje: "Revisa la configuración de IA" (i18n es/en). Sin alert
  bloqueante; el botón vuelve a estado normal de inmediato.

### 10. Unificación del detalle de playlist en Organizar → Por lista

- Ruta nueva `/organizar/lista/:id` (y `/organize/list/:id`): abre Organizar en modo
  "Por lista" con esa playlist preseleccionada.
- En home, el título de cada tarjeta navega a esa ruta (antes `/playlists/{id}`).
- `/playlists/:id` y `/listas/:id` redirigen a la ruta nueva (compatibilidad).
- Las acciones del detalle se integran como barra de acciones del modo "Por lista":
  Buscar repetidas (dentro de la lista, con estrategia mismo video / mismo título),
  Eliminar repetidas, Ordenar con IA (con su timeout y mensaje de error del punto 9).
  Los resultados (grupos de duplicados con "conservar esta" / "quitar copia", y la
  clasificación IA) se muestran en la misma vista.
- El componente `PlaylistDetail` se elimina. El punto 6 (título del documento) y el
  tooltip de IA (punto 9) aplican ahora dentro de Organizar cuando hay lista elegida.

## Componentes afectados

| Capa | Archivo | Cambio |
|---|---|---|
| Front | `app.routes.ts` | `buscar`/`search` → redirect a organizar; quitar lazy import SongSearch |
| Front | `app.ts` | quitar ítem de menú Buscar |
| Front | `components/song-search/*` | eliminar |
| Front | `pages/cross-duplicates/*` | filtros en vivo + scope, badges editables, guardar inline, modal sin una/varias, tag "No disponible" |
| Front | `pages/playlists/*` | fecha relativa + orden por modificación |
| Front | `pages/playlist-detail/*` | señal `playlistTitle` desde caché; `Title` del documento; tooltip error IA |
| Front | `models.ts` | `Playlist.lastModifiedUtc?` |
| Front | i18n `es.json`/`en.json` | claves nuevas; limpiar claves `search.*` huérfanas |
| Back | `YouTubeService.cs` | filtro `IsUnavailable` en ambas detecciones; `lastModifiedUtc` en playlists |
| Back | `SongSearchService.cs` | scope real + exclusión no disponibles; conteo de repetidas por playlist |
| Back | `SongsController.cs` | endpoint `duplicate-counts` (cache-only) |
| Back | `NvidiaClassifier.cs` | timeout HTTP ~12 s + error claro |
| Back | `DTOs.cs` | `PlaylistDto.LastModifiedUtc?` |

## Errores y casos borde

- Caché sin playlist buscada por id en detalle → fallback al título genérico actual.
- Log de actividad vacío o playlist sin eventos → sin fecha, va al final del orden.
- Canción cuyo único hogar se quita vía badges → `assignSong` con lista vacía es válido
  (remoción total); el flujo staged existente ya lo soporta.
- Búsqueda con scope `archived` y cero playlists archivadas → resultado vacío, no error.
- Playlist sin items cacheados → sin conteo de repetidas en el selector (no "0" engañoso).
- IA sin conectividad/API key → error en ≤12 s con mensaje "Revisa la configuración de IA";
  nunca cuelga el botón.

## Pruebas

- Backend: unit tests de `IsUnavailable` (títulos exactos, case, títulos normales que contienen
  "private"), scope en `SearchCombined`, exclusión en ambos métodos de duplicados.
- Front: build limpio (`ng build`); verificación manual de flujos: redirect /buscar, filtrado
  en vivo, badge × → guardar → aparece en panel pendientes, título de detalle al navegar
  directo, orden por fecha en home.

## Fuera de alcance

- Tracking real de modificaciones históricas anteriores al log de actividad.
- Eliminación automática de videos no disponibles.
- Cambios al panel global de pendientes.
