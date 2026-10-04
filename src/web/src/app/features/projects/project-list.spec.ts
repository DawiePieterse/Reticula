import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { AUTH_STORAGE_KEY } from '../../core/auth/auth.service';
import { ProjectList } from './project-list';

const signIn = (roles: string[]) =>
  localStorage.setItem(
    AUTH_STORAGE_KEY,
    JSON.stringify({ tokens: { accessToken: 'a', refreshToken: 'r', expiresAt: 0 }, user: { id: '1', email: 'x', displayName: 'X', registrationNo: null, roles } }),
  );

const project = {
  id: 'p1', name: 'Soshanguve Ext 19', rulesRef: 'eskom/0.1.0', authority: 'eskom',
  area: { type: 'Polygon', coordinates: [] }, createdAt: '2026-10-03T10:00:00Z', updatedAt: '2026-10-03T10:00:00Z', version: 1,
};

async function render(roles: string[]) {
  localStorage.clear();
  signIn(roles);
  TestBed.configureTestingModule({
    imports: [ProjectList],
    providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
  });
  const fixture = TestBed.createComponent(ProjectList);
  TestBed.inject(HttpTestingController).expectOne('/api/projects').flush([project]);
  await fixture.whenStable();
  return fixture.nativeElement as HTMLElement;
}

describe('ProjectList', () => {
  it('lists projects and offers New project to the engineer', async () => {
    const el = await render(['engineer']);
    expect(el.textContent).toContain('Soshanguve Ext 19');
    expect(el.querySelector('a.button.primary')?.textContent).toContain('New project');
  });

  it('hides New project from inspectors', async () => {
    const el = await render(['inspector']);
    expect(el.textContent).toContain('Soshanguve Ext 19');
    expect(el.querySelector('a.button.primary')).toBeNull();
  });
});
