import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { ConnectivityService } from '../connectivity.service';
import { AuthService } from './auth.service';

export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.isAuthenticated() ? true : router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};

export const engineerGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.isEngineer() ? true : router.createUrlTree(['/projects']);
};

/** For screens that need the server. Field capture screens (Phase 1) will not use this. */
export const onlineGuard: CanActivateFn = () => {
  const online = inject(ConnectivityService).online();
  const router = inject(Router);
  return online ? true : router.createUrlTree(['/offline']);
};
