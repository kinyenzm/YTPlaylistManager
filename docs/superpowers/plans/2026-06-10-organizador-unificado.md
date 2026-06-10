# Organizador Unificado Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Unificar /buscar dentro de Organizar, excluir videos privados/eliminados de todas las detecciones, badges de playlists editables con guardado inline, fecha de modificación local en home, título de detalle/pestaña siempre resuelto, contador de repetidas por lista y timeout del clasificador IA.

**Architecture:** Backend .NET 10 local-first (caches JSON en `./data`); helper estático `VideoAvailability` compartido por las 3 detecciones; store nuevo `PlaylistTouchStore` (id→fecha) tocado en cada mutación local; scope de búsqueda vía `ArchivedPlaylistsStore`. Frontend Angular 21 signals/OnPush: CrossDuplicates absorbe la búsqueda en vivo, SongSearch se elimina.

**Tech Stack:** .NET 10, ASP.NET controllers, System.Text.Json, xUnit (proyecto nuevo), Angular 21 standalone + signals, @ngx-translate, Tailwind-less CSS global existente.

**REGLA DE SESIÓN (orden directa del usuario):** NO hacer commits intermedios. Los pasos de commit están agrupados al FINAL (Task 14). NUNCA hacer push.

**REGLA DE SESIÓN 2 (usuario, 2026-06-10):** NO agregar el proyecto de tests xUnit todavía.
Task 1 queda DIFERIDA y los pasos TDD de Tasks 2/4/5 se reemplazan por verificación de build
(`dotnet build YTPlaylistManager.Server`) + verificación manual de Task 13. Nota: si el backend
está corriendo, el build falla solo en la copia final del .exe (MSB3027); los errores de
compilación aparecen antes, así que ese fallo de copia cuenta como build OK.

---

## Spec

`docs/superpowers/specs/2026-06-10-organizador-unificado-design.md` (commit `d02f9ba` + ediciones sin commitear con puntos 7-9).

## Verificación global

- Backend: `dotnet build YTPlaylistManager.Server` y `dotnet test YTPlaylistManager.Server.Tests`
- Frontend: `npm run build` dentro de `YTPlaylistManager.client` (usa `ng build`)
- Manual: backend `dotnet run --project YTPlaylistManager.Server` (puerto 5080) + `npm start` (4200)

---

### Task 1: Proyecto de tests xUnit

**Files:**
- Create: `YTPlaylistManager.Server.Tests/YTPlaylistManager.Server.Tests.csproj`

- [ ] **Step 1: Crear proyecto y referenciarlo**

```powershell
dotnet new xunit -n YTPlaylistManager.Server.Tests -o YTPlaylistManager.Server.Tests
dotnet add YTPlaylistManager.Server.Tests reference YTPlaylistManager.Server
```

Si existe `YTPlaylistManager.sln`: `dotnet sln add YTPlaylistManager.Server.Tests` (si no existe sln, omitir).

- [ ] **Step 2: Verificar que compila y corre**

Run: `dotnet test YTPlaylistManager.Server.Tests`
Expected: PASS (1 test placeholder `UnitTest1`). Borrar `UnitTest1.cs` después.

---

### Task 2: Helper `VideoAvailability` (TDD)

**Files:**
- Create: `YTPlaylistManager.Server/Services/VideoAvailability.cs`
- Test: `YTPlaylistManager.Server.Tests/VideoAvailabilityTests.cs`

- [ ] **Step 1: Test que falla**

```csharp
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Tests;

public class VideoAvailabilityTests
{
    [Theory]
    [InlineData("Private video")]
    [InlineData("Deleted video")]
    [InlineData("private video")]
    [InlineData("DELETED VIDEO")]
    [InlineData("  Private video  ")]
    public void IsUnavailable_TitulosDeVideosInaccesibles_True(string title)
        => Assert.True(VideoAvailability.IsUnavailable(title));

    [Theory]
    [InlineData("Mi video privado favorito")]
    [InlineData("Deleted video - official remix")]
    [InlineData("(sin título)")]
    [InlineData("")]
    [InlineData(null)]
    public void IsUnavailable_TitulosNormales_False(string? title)
        => Assert.False(VideoAvailability.IsUnavailable(title));
}
```

- [ ] **Step 2: Verificar que falla**

Run: `dotnet test YTPlaylistManager.Server.Tests`
Expected: FAIL — `VideoAvailability` no existe.

- [ ] **Step 3: Implementación mínima**

```csharp
namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Detección de items de playlist sin video accesible. La YouTube API devuelve
/// estos títulos literales (siempre en inglés) cuando el video es privado o fue
/// eliminado; el item no tiene snippet real y no debe contar como canción.
/// </summary>
public static class VideoAvailability
{
    public static bool IsUnavailable(string? title)
    {
        var t = title?.Trim();
        return string.Equals(t, "Private video", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "Deleted video", StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Verificar que pasa**

Run: `dotnet test YTPlaylistManager.Server.Tests`
Expected: PASS. (Sin commit — regla de sesión.)

---

### Task 3: Excluir no disponibles en las 2 detecciones de duplicados

**Files:**
- Modify: `YTPlaylistManager.Server/Services/YouTubeService.cs:313` (FindDuplicatesAsync, grupo videoId)
- Modify: `YTPlaylistManager.Server/Services/YouTubeService.cs:323` (FindDuplicatesAsync, grupo normalizedTitle)
- Modify: `YTPlaylistManager.Server/Services/YouTubeService.cs:361-370` (FindCrossDuplicatesAsync)

No hay test directo (método llama YouTube API); la lógica filtrante es `VideoAvailability` ya testeada. Verificación por build + manual.

- [ ] **Step 1: FindDuplicatesAsync — grupo por videoId**

En `YouTubeService.cs:313`, agregar filtro:

```csharp
        // Por videoId
        foreach (var g in items
                     .Where(x => !string.IsNullOrEmpty(x.VideoId) && !VideoAvailability.IsUnavailable(x.Title))
                     .GroupBy(x => x.VideoId)
                     .Where(g => g.Count() > 1))
```

- [ ] **Step 2: FindDuplicatesAsync — grupo por título normalizado**

En `YouTubeService.cs:323`:

```csharp
        foreach (var g in items
                     .Where(x => !VideoAvailability.IsUnavailable(x.Title))
                     .GroupBy(x => Normalize(x.Title))
                     .Where(g => g.Count() > 1 && !string.IsNullOrWhiteSpace(g.Key)))
```

- [ ] **Step 3: FindCrossDuplicatesAsync**

En `YouTubeService.cs:361-363`, dentro del `foreach (var it in items)`:

```csharp
            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.VideoId)) continue;
                if (VideoAvailability.IsUnavailable(it.Title)) continue;
```

- [ ] **Step 4: Build**

Run: `dotnet build YTPlaylistManager.Server`
Expected: 0 errores.

---

### Task 4: Scope real + exclusión en búsqueda (TDD)

**Files:**
- Modify: `YTPlaylistManager.Server/Services/SongSearchService.cs`
- Test: `YTPlaylistManager.Server.Tests/SongSearchServiceTests.cs`

- [ ] **Step 1: Tests que fallan**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using YTPlaylistManager.Server.DTOs;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Tests;

public class SongSearchServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ytpm-tests-" + Guid.NewGuid());
    private readonly SongSearchService _svc;
    private readonly ArchivedPlaylistsStore _archived;

    public SongSearchServiceTests()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DataFolder"] = _tempDir })
            .Build();

        var cacheStore = new PlaylistCacheStore(cfg);
        var itemsCache = new PlaylistItemsCacheStore(cfg);
        _archived = new ArchivedPlaylistsStore(cfg);

        cacheStore.Save(new PlaylistCache
        {
            UserKey = "u1",
            CachedAtUtc = DateTime.UtcNow,
            Playlists =
            [
                new PlaylistDto("PL1", "Pop", null, 3, null),
                new PlaylistDto("PL2", "Vieja", null, 2, null),
            ],
        });
        itemsCache.Save("u1", "PL1",
        [
            new PlaylistItemDto("i1", "v1", "Shape of You", "Ed", 0, null),
            new PlaylistItemDto("i2", "v2", "Private video", null, 1, null),
            new PlaylistItemDto("i3", "v3", "Hello", "Adele", 2, null),
        ]);
        itemsCache.Save("u1", "PL2",
        [
            new PlaylistItemDto("i4", "v1", "Shape of You", "Ed", 0, null),
            new PlaylistItemDto("i5", "v2", "Private video", null, 1, null),
        ]);

        _svc = new SongSearchService(cacheStore, itemsCache, _archived, NullLogger<SongSearchService>.Instance);
    }

    [Fact]
    public void Search_ExcluyeVideosNoDisponibles()
    {
        var r = _svc.SearchCombined("v2", "private", "all");
        Assert.Empty(r);
    }

    [Fact]
    public void Search_ScopeActive_ExcluyeArchivadas()
    {
        _archived.Add([new ArchivedPlaylistEntry
        {
            Id = "PL2", Title = "Vieja", ArchivedAtUtc = DateTime.UtcNow,
            MergedIntoPlaylistId = "PL1", MergedIntoPlaylistTitle = "Pop", SongsCount = 2,
        }]);
        var r = _svc.SearchCombined(null, "shape", "active");
        Assert.Single(r);
        Assert.Equal("PL1", r[0].CurrentPlaylistId);
    }

    [Fact]
    public void Search_ScopeArchived_SoloArchivadas()
    {
        _archived.Add([new ArchivedPlaylistEntry
        {
            Id = "PL2", Title = "Vieja", ArchivedAtUtc = DateTime.UtcNow,
            MergedIntoPlaylistId = "PL1", MergedIntoPlaylistTitle = "Pop", SongsCount = 2,
        }]);
        var r = _svc.SearchCombined(null, "shape", "archived");
        Assert.Single(r);
        Assert.Equal("PL2", r[0].CurrentPlaylistId);
    }

    [Fact]
    public void Search_ScopeAll_TodasLasOcurrencias()
    {
        var r = _svc.SearchCombined(null, "shape", "all");
        Assert.Equal(2, r.Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
```

Nota: si `PlaylistCacheStore.Save` o `PlaylistItemsCacheStore.Save` tienen otra firma, ajustar el arrange al API real del store (leerlos antes); el assert no cambia.

- [ ] **Step 2: Verificar que falla**

Run: `dotnet test YTPlaylistManager.Server.Tests`
Expected: FAIL — constructor de `SongSearchService` no acepta `ArchivedPlaylistsStore`.

- [ ] **Step 3: Implementación**

En `SongSearchService.cs`:

1. Inyectar store (campo + constructor):

```csharp
    private readonly PlaylistCacheStore _cacheStore;
    private readonly PlaylistItemsCacheStore _itemsCache;
    private readonly ArchivedPlaylistsStore _archivedStore;
    private readonly ILogger<SongSearchService> _logger;

    public SongSearchService(
        PlaylistCacheStore cacheStore,
        PlaylistItemsCacheStore itemsCache,
        ArchivedPlaylistsStore archivedStore,
        ILogger<SongSearchService> logger)
    {
        _cacheStore = cacheStore;
        _itemsCache = itemsCache;
        _archivedStore = archivedStore;
        _logger = logger;
    }
```

2. Excluir no disponibles en `LoadAllCachedItems` (cubre las dos rutas de búsqueda y el índice de apariciones, para que un video privado tampoco infle `AppearsInCount`):

```csharp
            foreach (var it in items)
            {
                if (VideoAvailability.IsUnavailable(it.Title)) continue;
                flat.Add(new FlatItem(pl.Id, pl.Title, it));
            }
```

3. Aplicar scope en `SearchCombined`:

```csharp
    public List<SongSearchResultDto> SearchCombined(string? videoIdPartial, string? songNameFuzzy, string searchScope)
    {
        var byId = string.IsNullOrWhiteSpace(videoIdPartial) ? [] : SearchByVideoId(videoIdPartial);
        var byName = string.IsNullOrWhiteSpace(songNameFuzzy) ? [] : SearchByName(songNameFuzzy);

        var union = byId
            .Concat(byName)
            .GroupBy(r => (r.VideoId, r.CurrentPlaylistId))
            .Select(g => g.First());

        // Scope sobre la playlist donde está la ocurrencia: active = no archivadas,
        // archived = solo archivadas, all (o valor desconocido) = sin filtro.
        var scope = (searchScope ?? "all").ToLowerInvariant();
        if (scope is "active" or "archived")
        {
            var archivedIds = _archivedStore.LoadAll().Select(e => e.Id).ToHashSet();
            union = scope == "active"
                ? union.Where(r => r.CurrentPlaylistId is null || !archivedIds.Contains(r.CurrentPlaylistId))
                : union.Where(r => r.CurrentPlaylistId is not null && archivedIds.Contains(r.CurrentPlaylistId));
        }

        return union.ToList();
    }
```

- [ ] **Step 4: Verificar que pasa**

Run: `dotnet test YTPlaylistManager.Server.Tests`
Expected: PASS (todos).

---

### Task 5: Conteo de repetidas por playlist (TDD)

**Files:**
- Modify: `YTPlaylistManager.Server/Services/SongSearchService.cs` (interfaz + implementación)
- Modify: `YTPlaylistManager.Server/Controllers/SongsController.cs`
- Test: `YTPlaylistManager.Server.Tests/SongSearchServiceTests.cs`

- [ ] **Step 1: Test que falla**

Agregar a `SongSearchServiceTests`:

```csharp
    [Fact]
    public void DuplicateCounts_CuentaVideosEnVariasListas_SinNoDisponibles()
    {
        // v1 está en PL1 y PL2 → cuenta 1 repetida en cada una.
        // v2 ("Private video") también está en ambas pero NO debe contar.
        var counts = _svc.GetDuplicateCountsByPlaylist();
        Assert.Equal(1, counts["PL1"]);
        Assert.Equal(1, counts["PL2"]);
    }
```

- [ ] **Step 2: Verificar que falla**

Run: `dotnet test YTPlaylistManager.Server.Tests`
Expected: FAIL — método no existe.

- [ ] **Step 3: Implementación**

En la interfaz `ISongSearchService` (mismo archivo, tras `SearchCombined`):

```csharp
    /// <summary>Por playlist: cuántos videos (disponibles) aparecen también en otra lista.</summary>
    Dictionary<string, int> GetDuplicateCountsByPlaylist();
```

En la clase:

```csharp
    public Dictionary<string, int> GetDuplicateCountsByPlaylist()
    {
        var flat = LoadAllCachedItems();
        var appearance = AppearanceIndex(flat);
        return flat
            .Where(x => !string.IsNullOrEmpty(x.Item.VideoId)
                        && appearance.TryGetValue(x.Item.VideoId, out var ids) && ids.Count > 1)
            .GroupBy(x => x.PlaylistId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Item.VideoId).Distinct().Count());
    }
```

- [ ] **Step 4: Verificar que pasa**

Run: `dotnet test YTPlaylistManager.Server.Tests`
Expected: PASS.

- [ ] **Step 5: Endpoint**

En `SongsController.cs`, después de `Search` (línea ~53):

```csharp
    /// <summary>Por playlist: cuántas canciones (videos disponibles) están también en otra lista. 100% caché, 0 cuota.</summary>
    [HttpGet("duplicate-counts")]
    [RequireGoogleSession]
    [ProducesResponseType<Dictionary<string, int>>(StatusCodes.Status200OK)]
    public IActionResult DuplicateCounts()
        => Ok(_searchService.GetDuplicateCountsByPlaylist());
```

- [ ] **Step 6: Build**

Run: `dotnet build YTPlaylistManager.Server`
Expected: 0 errores.

---

### Task 6: `PlaylistTouchStore` + `lastModifiedUtc`

**Files:**
- Create: `YTPlaylistManager.Server/Services/PlaylistTouchStore.cs`
- Modify: `YTPlaylistManager.Server/DTOs/DTOs.cs:5-17` (PlaylistDto)
- Modify: `YTPlaylistManager.Server/Services/YouTubeService.cs` (inyección + Touch en mutaciones + anotación)
- Modify: `YTPlaylistManager.Server/Program.cs:25` (DI)

- [ ] **Step 1: Store nuevo**

```csharp
using System.Text.Json;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Última modificación LOCAL por playlist (staging de canciones, merges, subidas).
/// YouTube no expone "última modificación" de una playlist; este registro la
/// aproxima con las acciones hechas desde la app. playlistId → fecha UTC.
/// </summary>
public class PlaylistTouchStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public PlaylistTouchStore(IConfiguration cfg)
    {
        var folder = cfg["Storage:DataFolder"] ?? "./data";
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "playlist-touch.json");
    }

    public Dictionary<string, DateTime> LoadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_path)) ?? [];
        }
    }

    public void Touch(params IEnumerable<string> playlistIds)
    {
        lock (_lock)
        {
            var map = LoadAll();
            var now = DateTime.UtcNow;
            foreach (var id in playlistIds.Where(id => !string.IsNullOrEmpty(id)))
                map[id] = now;
            File.WriteAllText(_path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
```

- [ ] **Step 2: Registrar en DI**

`Program.cs`, junto a los otros stores:

```csharp
builder.Services.AddSingleton<PlaylistTouchStore>();
```

- [ ] **Step 3: DTO**

`PlaylistDto` gana parámetro opcional al final:

```csharp
public record PlaylistDto(
    string Id,
    string Title,
    string? Description,
    int ItemCount,
    string? ThumbnailUrl,
    string? Privacy = null,           // "public" | "private" | "unlisted"
    bool IsArchived = false,          // local-only: marcada como consolidada
    string? ArchivedIntoPlaylistId = null,
    string? ArchivedIntoPlaylistTitle = null,
    bool QueuedForMerge = false,      // en cola: sus canciones ya se unieron (local); se borrará al subir
    string? QueuedIntoTitle = null,
    DateTime? LastModifiedUtc = null  // última acción local sobre la lista (PlaylistTouchStore)
);
```

- [ ] **Step 4: Inyectar y anotar en YouTubeService**

1. Campo + parámetro de constructor `PlaylistTouchStore touchStore` (constructor actual ya recibe varios stores; agregar al final igual que `_itemsCache` etc.) → `_touchStore = touchStore;`
2. Método privado:

```csharp
    private List<PlaylistDto> AnnotateTouched(List<PlaylistDto> playlists)
    {
        var touched = _touchStore.LoadAll();
        return playlists
            .Select(p => touched.TryGetValue(p.Id, out var at) ? p with { LastModifiedUtc = at } : p)
            .ToList();
    }
```

3. En `GetMyPlaylistsAsync` envolver los DOS retornos con caché y el retorno fresco (líneas 155, 189, 197):

```csharp
return AnnotateTouched(AnnotateQueued(AnnotateArchived(cache.Playlists, includeArchived)));
// ...
return AnnotateTouched(AnnotateQueued(AnnotateArchived(ordered, includeArchived)));
```

- [ ] **Step 5: Touch en cada mutación local**

Buscar con grep los métodos públicos de mutación y añadir al final de cada uno (antes del return):

- `StageSongAssignment(AssignSongRequest req)` → `_touchStore.Touch(idsAfectados);` donde `idsAfectados` = listas agregadas + quitadas (el método ya calcula ambas; usar esas colecciones de ids).
- `StageRemoveFromPlaylist(string playlistId, ...)` → `_touchStore.Touch(playlistId);`
- `StageRemoveItemsFromPlaylist(string playlistId, ...)` → `_touchStore.Touch(playlistId);`
- `MergePlaylistsAsync(...)` (staging local del merge) → `_touchStore.Touch(req.TargetPlaylistId ?? targetId)` según variable local del target.
- `RemoveDuplicatesAsync(...)` → `_touchStore.Touch(req.PlaylistId);`

Las subidas a YouTube no necesitan touch propio: la fecha local ya quedó al hacer staging.

- [ ] **Step 6: Build + tests**

Run: `dotnet build YTPlaylistManager.Server && dotnet test YTPlaylistManager.Server.Tests`
Expected: 0 errores, tests PASS.

---

### Task 7: Timeout + error claro en clasificador IA

**Files:**
- Create: `YTPlaylistManager.Server/Domain/Exceptions/AiUnavailableException.cs`
- Modify: `YTPlaylistManager.Server/Services/NvidiaClassifier.cs:45-49,67,128-131,190-198`
- Modify: `YTPlaylistManager.Server/Middleware/GlobalExceptionMiddleware.cs:38`
- Modify: `YTPlaylistManager.Server/Program.cs:13`

- [ ] **Step 1: Excepción**

```csharp
namespace YTPlaylistManager.Server.Domain.Exceptions;

/// <summary>El clasificador IA no está disponible (sin API key, sin red, o todos los modelos fallaron).</summary>
public sealed class AiUnavailableException(string message) : Exception(message);
```

- [ ] **Step 2: Cliente HTTP con timeout**

`Program.cs`:

```csharp
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("nvidia", c => c.Timeout = TimeSpan.FromSeconds(12));
```

- [ ] **Step 3: NvidiaClassifier**

1. `using YTPlaylistManager.Server.Domain.Exceptions;`
2. Sin API key → error en vez de heurística silenciosa (líneas 45-49):

```csharp
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("TU_"))
        {
            _logger.LogWarning("Sin API key de NVIDIA configurada (Ai:NvidiaApiKey).");
            throw new AiUnavailableException("Falta la API key de NVIDIA (Ai:NvidiaApiKey).");
        }
```

3. Cliente nombrado (línea 67): `var http = _httpFactory.CreateClient("nvidia");`
4. Fallback final (líneas 128-130) → error:

```csharp
        _logger.LogError("Todos los modelos del catálogo de NVIDIA fallaron o no están disponibles.");
        throw new AiUnavailableException("Ningún modelo de IA respondió (revisa conectividad y configuración).");
```

5. Eliminar `HeuristicClassify` completo (queda muerto).

- [ ] **Step 4: Middleware → 503**

En `GlobalExceptionMiddleware.cs`, antes del catch de `HttpRequestException`:

```csharp
        catch (AiUnavailableException ex)
        {
            // El frontend muestra "Revisa la configuración de IA" cuando recibe 503 del classify.
            logger.LogWarning(ex, "IA no disponible: {Message}", ex.Message);
            await WriteErrorResponse(context, HttpStatusCode.ServiceUnavailable, ex.Message);
        }
```

- [ ] **Step 5: Build**

Run: `dotnet build YTPlaylistManager.Server`
Expected: 0 errores.

---

### Task 8: Frontend base — modelos + ApiService

**Files:**
- Modify: `YTPlaylistManager.client/src/app/models/models.ts:1-13`
- Modify: `YTPlaylistManager.client/src/app/services/api.service.ts`

- [ ] **Step 1: Modelo**

```typescript
export interface Playlist {
  id: string;
  title: string;
  description?: string;
  itemCount: number;
  thumbnailUrl?: string;
  privacy?: string;            // "public" | "private" | "unlisted"
  isArchived?: boolean;
  archivedIntoPlaylistId?: string | null;
  archivedIntoPlaylistTitle?: string | null;
  queuedForMerge?: boolean;
  queuedIntoTitle?: string | null;
  lastModifiedUtc?: string | null;   // última acción local sobre la lista
}
```

- [ ] **Step 2: ApiService**

Junto a `searchSongs`:

```typescript
  duplicateCounts(): Observable<Record<string, number>> {
    return this.http.get<Record<string, number>>(`${this.base}/songs/duplicate-counts`);
  }
```

- [ ] **Step 3: Build**

Run (en `YTPlaylistManager.client`): `npm run build`
Expected: build OK.

---

> **CAMBIO DE ALCANCE (usuario, 2026-06-10):** el detalle de playlist se UNIFICA dentro de
> Organizar → Por lista. Task 9 pasa a implementarse así:
> - Ruta `organizar/lista/:id` + `organize/list/:id` → CrossDuplicates (input opcional `id`
>   vía component input binding; en constructor: si `id()` → mode 'byList' + `pickList(id)`).
> - `listas/:id` y `playlists/:id` → `redirectTo: 'organizar/lista/:id'`.
> - Home: link del título → `['/organizar/lista', p.id]`.
> - Las acciones del detalle (Buscar repetidas + estrategia, Eliminar repetidas, Ordenar con
>   IA + modo, grupos de duplicados con conservar/quitar, clasificación IA, busy overlays)
>   se PORTAN a CrossDuplicates visibles solo en modo byList con lista elegida — combinar,
>   no borrar (orden del usuario).
> - Título del documento (pestaña) y señal `playlistTitle` se implementan en CrossDuplicates
>   al elegir lista. Tooltip IA (503 → 'cross.ai_config_error') también ahí.
> - `pages/playlist-detail/` se ELIMINA en Task 12 tras portar todo.
> Lo que sigue de la Task 9 original queda como referencia del código a portar (h3/Title/
> aiError/classify) pero aplicado a `cross-duplicates.ts`.

### Task 9 (original, ahora referencia): Detalle de playlist — título siempre resuelto + título de pestaña + tooltip IA

**Files:**
- Modify: `YTPlaylistManager.client/src/app/pages/playlist-detail/playlist-detail.ts`
- Modify: `YTPlaylistManager.client/src/app/pages/playlist-detail/playlist-detail.html:1-23,61-63`
- Modify: `YTPlaylistManager.client/src/public/i18n/es.json` y `en.json` (ubicación real: buscar `detail.default_title` con grep)

- [ ] **Step 1: Componente**

En `playlist-detail.ts`:

1. Imports: `import { Title } from '@angular/platform-browser';` y `DestroyRef` desde `@angular/core`.
2. Inyecciones y señales nuevas:

```typescript
  private readonly titleSvc = inject(Title);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly playlistTitle = signal<string | null>(null);
  protected readonly aiError = signal<string | null>(null);
```

3. En el constructor, dentro del `effect` existente que reacciona a `id()`, resolver el nombre desde la caché de playlists (0 cuota) y fijar el título del documento:

```typescript
    effect(() => {
      const playlistId = this.id();
      if (!playlistId) return;
      this.refreshItems();
      this.duplicates.set(null);
      this.classification.set(null);
      this.stagedMsg.set(null);
      this.playlistTitle.set(null);
      this.api.listPlaylists(false, true).subscribe({
        next: (all) => {
          const t = all.find((p) => p.id === playlistId)?.title ?? null;
          this.playlistTitle.set(t);
          if (t) this.titleSvc.setTitle(`${t} — ${this.translate.instant('app.title')}`);
        },
        error: () => this.playlistTitle.set(null),
      });
    });

    this.destroyRef.onDestroy(() =>
      this.titleSvc.setTitle(this.translate.instant('app.title')));
```

4. `classify()` con manejo de error IA:

```typescript
  classify(): void {
    this.classifying.set(true);
    this.aiError.set(null);
    this.api.classify(this.id(), this.mode()).subscribe({
      next: (r) => {
        this.classification.set(r);
        this.classifying.set(false);
      },
      error: (e) => {
        this.classifying.set(false);
        this.aiError.set(
          e?.status === 503
            ? this.translate.instant('detail.ai_config_error')
            : this.translate.instant('detail.ai_generic_error'),
        );
      },
    });
  }
```

- [ ] **Step 2: Template**

`playlist-detail.html` línea 2:

```html
<h3>{{ playlistTitle() || duplicates()?.playlistTitle || ('detail.default_title' | translate) }}</h3>
```

Tras el botón clasificar (línea 21), tooltip de error:

```html
    <button (click)="classify()" [disabled]="classifying()" [title]="aiError() ?? ''">{{ 'detail.classify' | translate }}</button>
    @if (aiError(); as ae) {
      <span class="tag" style="background:var(--accent);color:#fff" role="alert">{{ ae }}</span>
    }
```

- [ ] **Step 3: i18n**

En `es.json` (sección `detail`):

```json
"ai_config_error": "Revisa la configuración de IA",
"ai_generic_error": "La clasificación falló; intenta de nuevo"
```

En `en.json`:

```json
"ai_config_error": "Check the AI configuration",
"ai_generic_error": "Classification failed; try again"
```

- [ ] **Step 4: Build + verificación manual**

`npm run build` OK. Manual: navegar directo a `/playlists/{id}` → h3 y pestaña muestran el nombre sin tocar "Buscar duplicados"; con backend IA sin key → botón clasifica muestra mensaje en ≤12 s.

---

### Task 10: Home — fecha de modificación + orden

**Files:**
- Modify: `YTPlaylistManager.client/src/app/pages/playlists/playlists-page.ts`
- Modify: `YTPlaylistManager.client/src/app/pages/playlists/playlists-page.html:49-89`
- Modify: i18n `es.json`/`en.json` (sección `playlists`)

- [ ] **Step 1: Orden + etiqueta relativa en el componente**

```typescript
  // Recientes primero (acción local registrada); sin fecha → después, alfabético.
  protected readonly playlistsSorted = computed<Playlist[]>(() => {
    return [...this.playlists()].sort((a, b) => {
      const ta = a.lastModifiedUtc ? Date.parse(a.lastModifiedUtc) : 0;
      const tb = b.lastModifiedUtc ? Date.parse(b.lastModifiedUtc) : 0;
      if (ta !== tb) return tb - ta;
      return a.title.localeCompare(b.title);
    });
  });

  modifiedAgo(iso: string): string {
    const ms = Date.parse(iso) - Date.now();
    const rtf = new Intl.RelativeTimeFormat(this.translate.currentLang || 'es', { numeric: 'auto' });
    const minutes = Math.round(ms / 60000);
    if (Math.abs(minutes) < 60) return rtf.format(minutes, 'minute');
    const hours = Math.round(minutes / 60);
    if (Math.abs(hours) < 24) return rtf.format(hours, 'hour');
    return rtf.format(Math.round(hours / 24), 'day');
  }
```

- [ ] **Step 2: Template**

Línea 50: iterar `playlistsSorted()` en vez de `playlists()`.

Dentro de la card, tras la fila de privacidad (línea 83):

```html
      @if (p.lastModifiedUtc) {
        <div class="muted" style="font-size:.85em;margin-top:4px">
          <i class="fa-regular fa-clock"></i> {{ 'playlists.modified' | translate:{ ago: modifiedAgo(p.lastModifiedUtc) } }}
        </div>
      }
```

- [ ] **Step 3: i18n**

`es.json` → `"modified": "Modificada {{ago}}"`. `en.json` → `"modified": "Modified {{ago}}"`.

- [ ] **Step 4: Build**

`npm run build` OK.

---

### Task 11: Organizar — búsqueda en vivo, badges editables, modal simplificado, no disponibles, contador

**Files:**
- Modify: `YTPlaylistManager.client/src/app/pages/cross-duplicates/cross-duplicates.ts`
- Modify: `YTPlaylistManager.client/src/app/pages/cross-duplicates/cross-duplicates.html`
- Modify: `YTPlaylistManager.client/src/styles.css` (estilos de badge editable; confirmar ruta con glob `src/styles.css`)
- Modify: i18n `es.json`/`en.json` (sección `cross`)

- [ ] **Step 1: TS — quitar una/varias, agregar filtros en vivo, removals staged, contador, no disponibles**

Cambios en `cross-duplicates.ts`:

1. Eliminar: `singleMode` señal, `setSingle()`, y la rama `if (this.singleMode())` de `toggle()`.
2. Búsqueda en vivo (reemplaza `term`/`search()` de un solo campo):

```typescript
  // Modo "por canción" — filtrado en vivo (debounce, sin Enter)
  protected readonly nameInput = signal<string>('');
  protected readonly idInput = signal<string>('');
  protected readonly searchScope = signal<'all' | 'active' | 'archived'>('all');
  protected readonly results = signal<SongSearchResult[]>([]);
  private readonly searchSubject = new Subject<void>();

  // en constructor:
    this.searchSubject.pipe(debounceTime(400)).subscribe(() => this.search());

  onFilterChange(): void {
    this.searchSubject.next();
  }

  search(): void {
    const name = this.nameInput().trim();
    const id = this.idInput().trim();
    if (!name && !id) {
      this.results.set([]);
      return;
    }
    this.loading.set(true);
    this.error.set(null);
    this.api.searchSongs({
      videoIdPartial: id || null,
      songNameFuzzy: name || null,
      searchScope: this.searchScope(),
    }).subscribe({
      next: (r) => {
        this.results.set(r);
        this.loading.set(false);
      },
      error: (e) => {
        this.error.set(this.translate.instant('cross.error_scan'));
        this.loading.set(false);
        console.error(e);
      },
    });
  }
```

Imports nuevos: `import { debounceTime, Subject } from 'rxjs';`. En `refreshCurrentMode()` la rama bySong queda `else if (m === 'bySong' && this.results().length) this.search();` (sin cambios de nombre).

3. Removals staged por tarjeta (badges):

```typescript
  // Eliminaciones preparadas por tarjeta: videoId → ids de listas marcadas para quitar.
  protected readonly stagedRemovals = signal<Record<string, ReadonlySet<string>>>({});

  removedSet(videoId: string): ReadonlySet<string> {
    return this.stagedRemovals()[videoId] ?? new Set();
  }

  toggleRemoval(videoId: string, playlistId: string): void {
    const all = { ...this.stagedRemovals() };
    const cur = new Set(all[videoId] ?? []);
    if (cur.has(playlistId)) cur.delete(playlistId);
    else cur.add(playlistId);
    if (cur.size === 0) delete all[videoId];
    else all[videoId] = cur;
    this.stagedRemovals.set(all);
  }

  discardCard(videoId: string): void {
    const all = { ...this.stagedRemovals() };
    delete all[videoId];
    this.stagedRemovals.set(all);
  }

  // Guardar sin modal: desired = listas actuales − marcadas. Entra al flujo staged
  // existente (assignSong → panel global de pendientes).
  saveCard(videoId: string, title: string, currentIds: string[]): void {
    const removed = this.removedSet(videoId);
    const desired = currentIds.filter((id) => !removed.has(id));
    this.applying.set(true);
    this.api.assignSong({ videoId, title, channelTitle: null, thumbnailUrl: null, desiredPlaylistIds: desired })
      .subscribe({
        next: () => {
          this.applying.set(false);
          this.discardCard(videoId);
          this.pendingSvc.refresh();
          this.refreshCurrentMode();
        },
        error: (e) => {
          this.error.set(this.translate.instant('cross.assign_error'));
          this.applying.set(false);
          console.error(e);
        },
      });
  }
```

4. Ids actuales por modo (para `saveCard` y badges): el modo repetidas ya tiene `g.playlists` (ids+títulos); por lista usa `locMap()[videoId]`; por canción `appearsInPlaylistIds`. Helper para byList/bySong que une id+título:

```typescript
  refsFor(ids: string[]): { id: string; title: string }[] {
    const t = this.titleById();
    return ids.map((id) => ({ id, title: t[id] ?? id }));
  }
```

5. No disponibles + contador de repetidas:

```typescript
  isUnavailable(title: string): boolean {
    const t = title?.trim().toLowerCase();
    return t === 'private video' || t === 'deleted video';
  }

  protected readonly dupCounts = signal<Record<string, number>>({});

  // en constructor:
    this.api.duplicateCounts().subscribe({
      next: (c) => this.dupCounts.set(c),
      error: (e) => console.error(e),
    });

  optionLabel(pl: Playlist): string {
    const base = `${pl.title} (${pl.itemCount})`;
    const n = this.dupCounts()[pl.id] ?? 0;
    return n > 0 ? `${base} — ${this.translate.instant('cross.dups_in_list', { n })}` : base;
  }
```

- [ ] **Step 2: HTML — modo por canción**

Reemplazar el bloque `mode() === 'bySong'` (líneas 104-132) por:

```html
@if (mode() === 'bySong') {
  <div class="row" style="margin-top:12px;gap:8px;flex-wrap:wrap">
    <input type="text" [ngModel]="nameInput()" (ngModelChange)="nameInput.set($event); onFilterChange()"
           (keyup.enter)="search()" [placeholder]="'cross.filter_name_ph' | translate" style="flex:2;min-width:200px" />
    <input type="text" [ngModel]="idInput()" (ngModelChange)="idInput.set($event); onFilterChange()"
           (keyup.enter)="search()" [placeholder]="'cross.filter_id_ph' | translate" style="flex:1;min-width:140px" />
    <select [ngModel]="searchScope()" (ngModelChange)="searchScope.set($event); onFilterChange()">
      <option value="all">{{ 'cross.scope_all' | translate }}</option>
      <option value="active">{{ 'cross.scope_active' | translate }}</option>
      <option value="archived">{{ 'cross.scope_archived' | translate }}</option>
    </select>
  </div>

  @if (results().length > 0) {
    <p class="muted" style="margin-top:12px">{{ 'cross.results_count' | translate:{ n: results().length } }}</p>
    @for (r of resultsSorted(); track r.videoId + r.originalPlaylistId) {
      <div class="card">
        <div class="row" style="align-items:flex-start;gap:8px">
          <img [src]="thumb(r.videoId)" class="thumb" alt="" />
          <div style="flex:1">
            <div>{{ r.title }}</div>
            <div class="muted">{{ r.channelTitle }}</div>
            <div class="row" style="gap:4px;flex-wrap:wrap;margin-top:4px">
              <span class="muted">{{ 'cross.appears_in' | translate }}</span>
              @for (ref of refsFor(r.appearsInPlaylistIds); track ref.id) {
                <span class="tag badge-edit" [class.staged-out]="removedSet(r.videoId).has(ref.id)">
                  {{ ref.title }}
                  <button type="button" class="badge-x" (click)="toggleRemoval(r.videoId, ref.id)"
                          [title]="(removedSet(r.videoId).has(ref.id) ? 'cross.badge_restore' : 'cross.badge_remove') | translate">
                    @if (removedSet(r.videoId).has(ref.id)) { <i class="fa-solid fa-rotate-left"></i> }
                    @else { <i class="fa-solid fa-xmark"></i> }
                  </button>
                </span>
              }
            </div>
            @if (removedSet(r.videoId).size > 0) {
              <div class="row" style="gap:8px;margin-top:8px">
                <button (click)="saveCard(r.videoId, r.title, r.appearsInPlaylistIds)" [disabled]="applying()">
                  {{ 'cross.save_changes' | translate:{ n: removedSet(r.videoId).size } }}
                </button>
                <button class="secondary" (click)="discardCard(r.videoId)">{{ 'common.cancel' | translate }}</button>
              </div>
            }
          </div>
          <button class="secondary" (click)="openEditor({ videoId: r.videoId, title: r.title })">
            {{ 'cross.assign_open' | translate }}
          </button>
        </div>
      </div>
    }
  } @else if (!loading() && (nameInput().trim() || idInput().trim())) {
    <div class="card" style="margin-top:10px">{{ 'cross.no_results' | translate }}</div>
  }
}
```

- [ ] **Step 3: HTML — mismos badges editables en "repetidas" y "por lista"**

Modo repetidas (línea 43), reemplazar `@for (p of g.playlists; ...)` por el mismo patrón de badge usando `p.playlistId`/`p.playlistTitle`, con guardar:

```html
              @for (p of g.playlists; track p.playlistId) {
                <span class="tag badge-edit" [class.staged-out]="removedSet(g.videoId).has(p.playlistId)">
                  {{ p.playlistTitle }}
                  <button type="button" class="badge-x" (click)="toggleRemoval(g.videoId, p.playlistId)"
                          [title]="(removedSet(g.videoId).has(p.playlistId) ? 'cross.badge_restore' : 'cross.badge_remove') | translate">
                    @if (removedSet(g.videoId).has(p.playlistId)) { <i class="fa-solid fa-rotate-left"></i> }
                    @else { <i class="fa-solid fa-xmark"></i> }
                  </button>
                </span>
              }
```

**IMPORTANTE (feedback del usuario):** en el modo "Por lista" la tarjeta completa es clickeable
(`toggleBulk`); la equis del badge y los botones guardar/deshacer DEBEN llamar
`$event.stopPropagation()` antes de su acción para no alternar la selección masiva:

```html
<button type="button" class="badge-x" (click)="$event.stopPropagation(); toggleRemoval(it.videoId, ref.id)" ...>
```

(aplicar el mismo patrón a los botones de `saveCard`/`discardCard` dentro de tarjetas de byList).

y debajo del row de badges:

```html
            @if (removedSet(g.videoId).size > 0) {
              <div class="row" style="gap:8px;margin-top:8px">
                <button (click)="saveCard(g.videoId, g.title, playlistIdsOf(g))" [disabled]="applying()">
                  {{ 'cross.save_changes' | translate:{ n: removedSet(g.videoId).size } }}
                </button>
                <button class="secondary" (click)="discardCard(g.videoId)">{{ 'common.cancel' | translate }}</button>
              </div>
            }
```

con helper en TS:

```typescript
  playlistIdsOf(g: CrossDuplicate): string[] {
    return g.playlists.map((p) => p.playlistId);
  }
```

(import `CrossDuplicate` desde models). Modo por lista (línea 85-90): igual, usando `refsFor(locMap()[it.videoId] ?? [])`, `saveCard(it.videoId, it.title, locMap()[it.videoId] ?? [])`. Además tag de no disponible junto al título del item:

```html
            <div>
              {{ it.title }}
              @if (isUnavailable(it.title)) {
                <span class="tag" style="background:var(--accent);color:#fff">{{ 'cross.unavailable_tag' | translate }}</span>
              }
            </div>
```

- [ ] **Step 4: HTML — selector por lista con contador + badge rojo**

Reemplazar el `<select>` del modo byList (líneas 59-64):

```html
    <select [ngModel]="listId()" (ngModelChange)="pickList($event)">
      <option value="">—</option>
      @for (pl of allPlaylists(); track pl.id) {
        <option [value]="pl.id">{{ optionLabel(pl) }}</option>
      }
    </select>
    @if (listId() && (dupCounts()[listId()] ?? 0) > 0) {
      <span class="dup-badge">{{ dupCounts()[listId()] }}</span>
    }
```

- [ ] **Step 4b: HTML — espacio entre selector y primer item (byList)**

Hoy el primer card queda pegado al selector. Envolver el listado del modo byList en un
contenedor con margen:

```html
  @if (listId() && listItems().length > 0) {
    <div style="margin-top:12px">
      <!-- card de selección masiva + @for de items, sin cambios internos -->
    </div>
  }
```

- [ ] **Step 5: HTML — modal sin "una/varias"**

Eliminar del modal (líneas 143-149) el bloque del segmented `cross.mode_label` / `mode_multi` / `mode_single` completo.

- [ ] **Step 6: CSS global**

En `src/styles.css` (final):

```css
/* Badges editables del organizador: × visible solo en hover; tachado = quitar preparado. */
.badge-edit { position: relative; padding-right: 6px; }
.badge-edit .badge-x {
  background: none; border: none; cursor: pointer; padding: 0 2px;
  font-size: .85em; color: inherit; opacity: 0; transition: opacity .15s;
}
.badge-edit:hover .badge-x { opacity: 1; }
.badge-edit.staged-out { text-decoration: line-through; opacity: .55; }
.badge-edit.staged-out .badge-x { opacity: 1; }
.dup-badge {
  background: #d33; color: #fff; border-radius: 999px;
  padding: 2px 8px; font-size: .8em; font-weight: 700;
}
```

- [ ] **Step 7: i18n**

Sección `cross` de `es.json`:

```json
"filter_name_ph": "Filtrar por nombre…",
"filter_id_ph": "ID de video…",
"scope_all": "Todas",
"scope_active": "Activas",
"scope_archived": "Archivadas",
"no_results": "Sin resultados",
"save_changes": "Guardar cambios ({{n}})",
"badge_remove": "Quitar de esta lista",
"badge_restore": "Restaurar",
"unavailable_tag": "No disponible",
"dups_in_list": "{{n}} repetidas"
```

`en.json`:

```json
"filter_name_ph": "Filter by name…",
"filter_id_ph": "Video ID…",
"scope_all": "All",
"scope_active": "Active",
"scope_archived": "Archived",
"no_results": "No results",
"save_changes": "Save changes ({{n}})",
"badge_remove": "Remove from this list",
"badge_restore": "Restore",
"unavailable_tag": "Unavailable",
"dups_in_list": "{{n}} duplicates"
```

Eliminar claves del modal: `cross.mode_label`, `cross.mode_multi`, `cross.mode_single` en ambos idiomas.

- [ ] **Step 8: Build**

`npm run build` OK.

---

### Task 12: Eliminar /buscar (fusión)

**Files:**
- Modify: `YTPlaylistManager.client/src/app/app.routes.ts:9-10,32-34`
- Modify: `YTPlaylistManager.client/src/app/app.ts:50,76-83`
- Delete: `YTPlaylistManager.client/src/app/components/song-search/` (carpeta completa)
- Modify: i18n `es.json`/`en.json`

- [ ] **Step 1: Rutas**

Quitar el lazy import `songSearch` y reemplazar las rutas:

```typescript
  // Buscar canción → fusionado en el organizador (modo "por canción")
  { path: 'buscar', redirectTo: 'organizar', pathMatch: 'full' },
  { path: 'search', redirectTo: 'organize', pathMatch: 'full' },
```

- [ ] **Step 2: Nav**

En `app.ts`: borrar la línea `<a [routerLink]="navPaths().search">…` del template y la entrada `search:` de `navPaths`.

- [ ] **Step 3: Borrar componente**

```powershell
Remove-Item -Recurse -Force YTPlaylistManager.client/src/app/components/song-search
```

- [ ] **Step 4: i18n — limpiar claves muertas**

Grep `'search.` en `src/` para confirmar que nadie más las usa; borrar el bloque `"search": { … }` y la clave `app.nav.search` de `es.json` y `en.json`. Conservar cualquier clave que siga referenciada (p. ej. si `cache-explorer` usa `cache.stat_total_songs`, esa es de `cache`, no de `search`).

- [ ] **Step 5: Build**

`npm run build`
Expected: OK; si falla por claves/imports residuales, el error indica el archivo exacto.

---

### Task 13: Verificación end-to-end manual

- [ ] **Step 1: Levantar todo**

```powershell
dotnet run --project YTPlaylistManager.Server   # :5080
# en otra terminal
cd YTPlaylistManager.client; npm start          # :4200
```

- [ ] **Step 2: Checklist**

1. `/buscar` redirige a `/organizar`; menú sin "Buscar".
2. Organizar → Por canción: escribir nombre filtra solo (sin Enter); filtro nombre primero, ID después; scope Archivadas devuelve solo archivadas.
3. Ningún "Private video"/"Deleted video" en: Repetidas, resultados de búsqueda, ni en duplicados del detalle ("mismo título").
4. Por lista: items no disponibles muestran tag "No disponible"; selector muestra "— N repetidas"; badge rojo junto al selector.
5. Hover en badge → ×; click → tachado + botón "Guardar cambios (n)"; guardar → aparece en panel global de pendientes sin abrir modal.
6. Modal de asignar sin control "una/varias".
7. Home: tarjetas con "Modificada hace…" tras hacer una acción (p. ej. quitar una canción), ordenadas recientes primero.
8. Detalle `playlist/{id}` navegando directo: h3 con nombre y pestaña "Nombre — App".
9. Sin API key de IA (o sin red): "Ordenar por IA" responde en ≤12 s con mensaje "Revisa la configuración de IA".

---

### Task 14: Commits finales (SOLO al terminar TODO; sin push)

- [ ] **Step 1: Commits agrupados por área**

```bash
git add YTPlaylistManager.Server YTPlaylistManager.Server.Tests
git commit -m "feat(server): excluye videos no disponibles, scope de búsqueda real, conteo de repetidas, fecha de modificación local y timeout de IA"

git add YTPlaylistManager.client
git commit -m "feat(client): fusiona buscar en organizar, badges editables con guardado inline, fecha de modificación y título de detalle/pestaña"

git add docs/
git commit -m "docs(spec): puntos 7-9 del organizador unificado + plan de implementación"
```

Cada mensaje lleva al final: `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` (vía HEREDOC).

- [ ] **Step 2: NO push.** Reportar al usuario hashes y resumen.
