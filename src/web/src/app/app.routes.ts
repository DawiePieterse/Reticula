import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', loadComponent: () => import('./features/system/system-status').then((m) => m.SystemStatus) },
  { path: '**', redirectTo: '' },
];
