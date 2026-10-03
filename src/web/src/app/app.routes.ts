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
      { path: 'system', loadComponent: () => import('./features/system/system-status').then((m) => m.SystemStatus) },
    ],
  },
  { path: '**', redirectTo: '' },
];
