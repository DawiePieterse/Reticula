import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AUTH_STORAGE_KEY, AuthService } from './auth.service';

export const TOKENS = { tokenType: 'Bearer', accessToken: 'access-1', expiresIn: 3600, refreshToken: 'refresh-1' };
export const ENGINEER_USER = { id: 'u1', email: 'e@x', displayName: 'Eng', registrationNo: null, roles: ['engineer'] };

describe('AuthService', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('logs in, loads the profile and persists the session', async () => {
    const done = auth.login('e@x', 'pw');
    http.expectOne({ method: 'POST', url: '/api/auth/login' }).flush(TOKENS);
    await Promise.resolve();
    http.expectOne('/api/auth/me').flush(ENGINEER_USER);
    await done;

    expect(auth.isAuthenticated()).toBe(true);
    expect(auth.isEngineer()).toBe(true);
    expect(auth.accessToken()).toBe('access-1');
    expect(JSON.parse(localStorage.getItem(AUTH_STORAGE_KEY)!).user.email).toBe('e@x');
  });

  it('logout clears the session and goes to login', () => {
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    localStorage.setItem(AUTH_STORAGE_KEY, JSON.stringify({ tokens: { accessToken: 'a', refreshToken: 'r', expiresAt: 0 }, user: ENGINEER_USER }));
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const restored = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
    expect(restored.isAuthenticated()).toBe(true);

    const nav2 = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    restored.logout();
    expect(restored.isAuthenticated()).toBe(false);
    expect(localStorage.getItem(AUTH_STORAGE_KEY)).toBeNull();
    expect(nav2).toHaveBeenCalledWith(['/login']);
    nav.mockRestore();
  });
});
