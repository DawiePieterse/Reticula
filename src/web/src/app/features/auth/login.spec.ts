import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { Login } from './login';

function type(el: HTMLElement, selector: string, value: string) {
  const input = el.querySelector<HTMLInputElement>(selector)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('Login', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [Login], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
  });

  it('signs in and goes to the return URL', async () => {
    const fixture = TestBed.createComponent(Login);
    fixture.componentRef.setInput('returnUrl', '/projects/p1');
    const http = TestBed.inject(HttpTestingController);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    type(el, 'input[type="email"]', 'e@test.local');
    type(el, 'input[type="password"]', 'secret-password');
    await fixture.whenStable();
    el.querySelector<HTMLButtonElement>('button[type="submit"]')!.click();

    http.expectOne('/api/auth/login').flush({ tokenType: 'Bearer', accessToken: 'a', expiresIn: 3600, refreshToken: 'r' });
    await Promise.resolve();
    http.expectOne('/api/auth/me').flush({ id: '1', email: 'e@test.local', displayName: 'E', registrationNo: null, roles: ['engineer'] });
    await fixture.whenStable();
    expect(nav).toHaveBeenCalledWith('/projects/p1');
  });

  it('shows the server message on failure', async () => {
    const fixture = TestBed.createComponent(Login);
    const http = TestBed.inject(HttpTestingController);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    type(el, 'input[type="email"]', 'e@test.local');
    type(el, 'input[type="password"]', 'wrong');
    await fixture.whenStable();
    el.querySelector<HTMLButtonElement>('button[type="submit"]')!.click();
    http.expectOne('/api/auth/login').flush({ detail: 'Invalid email or password.' }, { status: 401, statusText: 'Unauthorized' });
    await fixture.whenStable();
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Invalid email or password.');
  });
});
