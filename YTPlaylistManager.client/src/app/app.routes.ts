import { Routes } from '@angular/router';

// Cada pantalla se registra en español y en inglés (ambas URLs resuelven al mismo
// componente). El menú usa la ruta del idioma activo (ver app.ts -> navPaths()).
const crossDuplicates = () =>
  import('./pages/cross-duplicates/cross-duplicates').then((m) => m.CrossDuplicates);
const cacheExplorer = () =>
  import('./components/cache-explorer/cache-explorer').then((m) => m.CacheExplorer);

export const routes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./pages/playlists/playlists-page').then((m) => m.PlaylistsPage),
  },

  // Organizar canciones (repetidas / por lista / por canción). La vista "por lista"
  // con :id absorbe el viejo detalle de playlist.
  { path: 'organizar', loadComponent: crossDuplicates },
  { path: 'organize', loadComponent: crossDuplicates },
  { path: 'organizar/lista/:id', loadComponent: crossDuplicates },
  { path: 'organize/list/:id', loadComponent: crossDuplicates },
  // alias viejos
  { path: 'repetidas', redirectTo: 'organizar', pathMatch: 'full' },
  { path: 'duplicates', redirectTo: 'organize', pathMatch: 'full' },
  { path: 'listas/:id', redirectTo: 'organizar/lista/:id' },
  { path: 'playlists/:id', redirectTo: 'organizar/lista/:id' },

  // Buscar canción → fusionado en el organizador (modo "por canción")
  { path: 'buscar', redirectTo: 'organizar', pathMatch: 'full' },
  { path: 'search', redirectTo: 'organize', pathMatch: 'full' },

  // Datos guardados / data (cache)
  { path: 'datos', loadComponent: cacheExplorer },
  { path: 'data', loadComponent: cacheExplorer },

  { path: '**', redirectTo: '' },
];
