import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { ActivatedRouteSnapshot, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { AUTH_STORAGE_KEY } from './auth.service';
import { authGuard, engineerGuard } from './guards';

const run = (guard: typeof authGuard, url = '/projects') =>
  TestBed.runInInjectionContext(() => guard({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot));

const signIn = (roles: string[]) =>
  localStorage.setItem(
    AUTH_STORAGE_KEY,
    JSON.stringify({ tokens: { accessToken: 'a', refreshToken: 'r', expiresAt: 0 }, user: { id: '1', email: 'x', displayName: 'X', registrationNo: null, roles } }),
  );

describe('guards', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  const setup = () => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideRouter([])] });

  it('authGuard sends anonymous users to login with a return URL', () => {
    setup();
    const result = run(authGuard, '/projects/abc') as UrlTree;
    expect(result.toString()).toBe('/login?returnUrl=%2Fprojects%2Fabc');
  });

  it('authGuard lets signed-in users through', () => {
    signIn(['inspector']);
    setup();
    expect(run(authGuard)).toBe(true);
  });

  it('engineerGuard blocks inspectors', () => {
    signIn(['inspector']);
    setup();
    expect((run(engineerGuard) as UrlTree).toString()).toBe('/projects');
  });

  it('engineerGuard allows engineers', () => {
    signIn(['engineer']);
    setup();
    expect(run(engineerGuard)).toBe(true);
  });
});
