import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';

const UNAUTHENTICATED_ENDPOINTS = ['/api/auth/login', '/api/auth/refresh'];

/** Adds the bearer token to API calls; on 401 refreshes once and retries. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith('/api/') || UNAUTHENTICATED_ENDPOINTS.includes(req.url)) return next(req);

  const auth = inject(AuthService);
  const token = auth.accessToken();
  const withToken = (t: string | null) => (t ? req.clone({ setHeaders: { Authorization: `Bearer ${t}` } }) : req);

  return next(withToken(token)).pipe(
    catchError((err: unknown) => {
      if (!(err instanceof HttpErrorResponse) || err.status !== 401 || !auth.hasRefreshToken()) {
        return throwError(() => err);
      }
      return auth.refresh().pipe(
        catchError((refreshErr: unknown) => {
          // Only a rejected refresh ends the session; a network failure (offline) keeps it.
          if (refreshErr instanceof HttpErrorResponse && refreshErr.status === 401) auth.logout();
          return throwError(() => refreshErr);
        }),
        switchMap((t) => next(withToken(t.accessToken))),
      );
    }),
  );
};
