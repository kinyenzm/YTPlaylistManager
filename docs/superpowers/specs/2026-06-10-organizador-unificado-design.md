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
  para que el usuario pueda limpiarlos de la lista, pero no se reportan como "aparece en otras
  listas".

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

## Componentes afectados

| Capa | Archivo | Cambio |
|---|---|---|
| Front | `app.routes.ts` | `buscar`/`search` → redirect a organizar; quitar lazy import SongSearch |
| Front | `app.ts` | quitar ítem de menú Buscar |
| Front | `components/song-search/*` | eliminar |
| Front | `pages/cross-duplicates/*` | filtros en vivo + scope, badges editables, guardar inline, modal sin una/varias, tag "No disponible" |
| Front | `pages/playlists/*` | fecha relativa + orden por modificación |
| Front | `pages/playlist-detail/*` | señal `playlistTitle` desde caché |
| Front | `models.ts` | `Playlist.lastModifiedUtc?` |
| Front | i18n `es.json`/`en.json` | claves nuevas; limpiar claves `search.*` huérfanas |
| Back | `YouTubeService.cs` | filtro `IsUnavailable` en ambas detecciones; `lastModifiedUtc` en playlists |
| Back | `SongSearchService.cs` | scope real + exclusión no disponibles |
| Back | `DTOs.cs` | `PlaylistDto.LastModifiedUtc?` |

## Errores y casos borde

- Caché sin playlist buscada por id en detalle → fallback al título genérico actual.
- Log de actividad vacío o playlist sin eventos → sin fecha, va al final del orden.
- Canción cuyo único hogar se quita vía badges → `assignSong` con lista vacía es válido
  (remoción total); el flujo staged existente ya lo soporta.
- Búsqueda con scope `archived` y cero playlists archivadas → resultado vacío, no error.

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
