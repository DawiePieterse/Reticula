import { Routes } from '@angular/router';
import { authGuard, engineerGuard, onlineGuard } from './core/auth/guards';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./features/auth/login').then((m) => m.Login) },
  { path: 'offline', loadComponent: () => import('./features/offline/offline').then((m) => m.Offline) },
  {
    path: '',
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'projects' },
      {
        path: 'projects',
        canActivate: [onlineGuard],
        loadComponent: () => import('./features/projects/project-list').then((m) => m.ProjectList),
      },
      {
        path: 'projects/new',
        canActivate: [engineerGuard, onlineGuard],
        loadComponent: () => import('./features/projects/project-edit').then((m) => m.ProjectEdit),
      },
      {
        path: 'projects/:id',
        canActivate: [onlineGuard],
        loadComponent: () => import('./features/projects/project-edit').then((m) => m.ProjectEdit),
      },
      {
        // Works offline from the tablet's copy of the project (docs/field.md, Offline).
        path: 'projects/:id/field',
        loadComponent: () => import('./features/field/field-page').then((m) => m.FieldPage),
      },
      {
        path: 'projects/:id/loads',
        canActivate: [onlineGuard],
        loadComponent: () => import('./features/field/loads-page').then((m) => m.LoadsPage),
      },
      {
        path: 'projects/:id/lv',
        canActivate: [onlineGuard],
        loadComponent: () => import('./features/design/lv-design-page').then((m) => m.LvDesignPage),
      },
      { path: 'system', loadComponent: () => import('./features/system/system-status').then((m) => m.SystemStatus) },
    ],
  },
  { path: '**', redirectTo: '' },
];
