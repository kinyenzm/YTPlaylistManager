import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./pages/playlists/playlists-page').then((m) => m.PlaylistsPage),
  },
  {
    path: 'playlists/:id',
    loadComponent: () =>
      import('./pages/playlist-detail/playlist-detail').then((m) => m.PlaylistDetail),
  },
  {
    path: 'repetidas',
    loadComponent: () =>
      import('./pages/cross-duplicates/cross-duplicates').then((m) => m.CrossDuplicates),
  },
  {
    path: 'buscar',
    loadComponent: () =>
      import('./components/song-search/song-search').then((m) => m.SongSearch),
  },
  {
    path: 'cache',
    loadComponent: () =>
      import('./components/cache-explorer/cache-explorer').then((m) => m.CacheExplorer),
  },
  { path: '**', redirectTo: '' },
];
