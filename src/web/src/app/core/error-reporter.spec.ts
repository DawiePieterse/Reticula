import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AUTH_STORAGE_KEY } from './auth/auth.service';
import { ReportingErrorHandler } from './error-reporter';

describe('ReportingErrorHandler', () => {
  let http: HttpTestingController;
  let handler: ReportingErrorHandler;

  const setup = (signedIn: boolean) => {
    localStorage.clear();
    if (signedIn) {
      localStorage.setItem(AUTH_STORAGE_KEY, JSON.stringify({ tokens: { accessToken: 'tok', refreshToken: 'r', expiresAt: 0 }, user: null }));
    }
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), ReportingErrorHandler],
    });
    http = TestBed.inject(HttpTestingController);
    handler = TestBed.inject(ReportingErrorHandler);
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
  };

  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
  });

  it('reports an error once with the bearer token', () => {
    setup(true);
    const err = new TypeError('x is undefined');
    handler.handleError(err);
    handler.handleError(err);
    const req = http.expectOne('/api/system/client-errors');
    expect(req.request.headers.get('Authorization')).toBe('Bearer tok');
    expect(req.request.body.message).toBe('TypeError: x is undefined');
    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('does not report when signed out', () => {
    setup(false);
    handler.handleError(new Error('boom'));
    http.expectNone('/api/system/client-errors');
  });

  it('survives a failing report', () => {
    setup(true);
    handler.handleError(new Error('boom'));
    http.expectOne('/api/system/client-errors').flush(null, { status: 500, statusText: 'Server Error' });
  });
});
