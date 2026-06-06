import { Routes } from '@angular/router';

// Cada pantalla se registra en español y en inglés (ambas URLs resuelven al mismo
// componente). El menú usa la ruta del idioma activo (ver app.ts -> navPaths()).
const playlistDetail = () =>
  import('./pages/playlist-detail/playlist-detail').then((m) => m.PlaylistDetail);
const crossDuplicates = () =>
  import('./pages/cross-duplicates/cross-duplicates').then((m) => m.CrossDuplicates);
const songSearch = () =>
  import('./components/song-search/song-search').then((m) => m.SongSearch);
const cacheExplorer = () =>
  import('./components/cache-explorer/cache-explorer').then((m) => m.CacheExplorer);

export const routes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./pages/playlists/playlists-page').then((m) => m.PlaylistsPage),
  },

  // Detalle de lista
  { path: 'listas/:id', loadComponent: playlistDetail },
  { path: 'playlists/:id', loadComponent: playlistDetail },

  // Organizar canciones (repetidas / por lista / por canción)
  { path: 'organizar', loadComponent: crossDuplicates },
  { path: 'organize', loadComponent: crossDuplicates },
  // alias viejos
  { path: 'repetidas', redirectTo: 'organizar' },
  { path: 'duplicates', redirectTo: 'organize' },

  // Buscar canción / search
  { path: 'buscar', loadComponent: songSearch },
  { path: 'search', loadComponent: songSearch },

  // Datos guardados / data (cache)
  { path: 'datos', loadComponent: cacheExplorer },
  { path: 'data', loadComponent: cacheExplorer },

  { path: '**', redirectTo: '' },
];
