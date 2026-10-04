import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { vi } from 'vitest';
import { authInterceptor } from './auth.interceptor';
import { AUTH_STORAGE_KEY, AuthService } from './auth.service';

describe('authInterceptor', () => {
  let client: HttpClient;
  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.setItem(
      AUTH_STORAGE_KEY,
      JSON.stringify({ tokens: { accessToken: 'old', refreshToken: 'r1', expiresAt: 0 }, user: null }),
    );
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting(), provideRouter([])],
    });
    client = TestBed.inject(HttpClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    localStorage.clear();
  });

  it('adds the bearer token to API calls only', () => {
    client.get('/api/projects').subscribe();
    client.get('/assets/x.json').subscribe();
    expect(http.expectOne('/api/projects').request.headers.get('Authorization')).toBe('Bearer old');
    expect(http.expectOne('/assets/x.json').request.headers.has('Authorization')).toBe(false);
  });

  it('refreshes once on 401 and retries with the new token', async () => {
    const result = firstValueFrom(client.get<string[]>('/api/projects'));
    http.expectOne('/api/projects').flush(null, { status: 401, statusText: 'Unauthorized' });
    const refresh = http.expectOne('/api/auth/refresh');
    expect(refresh.request.body).toEqual({ refreshToken: 'r1' });
    expect(refresh.request.headers.has('Authorization')).toBe(false);
    refresh.flush({ tokenType: 'Bearer', accessToken: 'new', expiresIn: 3600, refreshToken: 'r2' });
    const retry = http.expectOne('/api/projects');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer new');
    retry.flush(['ok']);
    expect(await result).toEqual(['ok']);
  });

  it('signs out when the refresh token is rejected', async () => {
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const result = firstValueFrom(client.get('/api/projects')).catch((e: unknown) => e);
    http.expectOne('/api/projects').flush(null, { status: 401, statusText: 'Unauthorized' });
    http.expectOne('/api/auth/refresh').flush(null, { status: 401, statusText: 'Unauthorized' });
    await result;
    expect(TestBed.inject(AuthService).isAuthenticated()).toBe(false);
    expect(nav).toHaveBeenCalledWith(['/login']);
  });

  it('keeps the session when refresh fails because the device is offline', async () => {
    const result = firstValueFrom(client.get('/api/projects')).catch((e: unknown) => e);
    http.expectOne('/api/projects').flush(null, { status: 401, statusText: 'Unauthorized' });
    http.expectOne('/api/auth/refresh').error(new ProgressEvent('error'));
    await result;
    expect(TestBed.inject(AuthService).isAuthenticated()).toBe(true);
  });
});
