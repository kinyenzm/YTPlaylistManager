import { Routes } from '@angular/router';
import { unsavedDraftsGuard } from './guards/unsaved-drafts.guard';

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

  // Organizar canciones (repetidas / por lista / por canción / recuperar).
  { path: 'organizar', loadComponent: crossDuplicates, canDeactivate: [unsavedDraftsGuard] },
  { path: 'organize', loadComponent: crossDuplicates, canDeactivate: [unsavedDraftsGuard] },
  { path: 'organizar/lista/:id', loadComponent: crossDuplicates, canDeactivate: [unsavedDraftsGuard] },
  { path: 'organize/list/:id', loadComponent: crossDuplicates, canDeactivate: [unsavedDraftsGuard] },

  // Datos guardados / data (cache)
  { path: 'datos', loadComponent: cacheExplorer },
  { path: 'data', loadComponent: cacheExplorer },

  { path: '**', redirectTo: '' },
];
