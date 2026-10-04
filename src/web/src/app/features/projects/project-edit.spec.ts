import { Component, input, output } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AUTH_STORAGE_KEY } from '../../core/auth/auth.service';
import { AreaMap } from './area-map';
import { GeoJsonPolygon } from './geo';
import { ProjectEdit } from './project-edit';
import { ProjectLayout } from './project-layout';

@Component({ selector: 'app-area-map', template: '' })
class AreaMapStub {
  readonly area = input<GeoJsonPolygon | null>(null);
  readonly editable = input(false);
  readonly areaChange = output<GeoJsonPolygon | null>();
  readonly stands = input<unknown>(null);
  readonly buildings = input<unknown>(null);
  readonly preview = input<unknown>(null);
  readonly mapFeatures = input<unknown>(null);
  readonly focusId = input<string | null>(null);
  readonly featureClick = output<string>();
}

@Component({ selector: 'app-project-layout', template: '' })
class ProjectLayoutStub {
  readonly projectId = input<string>();
  readonly canEdit = input(false);
  readonly layersChange = output<unknown>();
  readonly focus = output<string>();
}

const AREA: GeoJsonPolygon = {
  type: 'Polygon',
  coordinates: [[[28.1, -25.52], [28.11, -25.52], [28.11, -25.51], [28.1, -25.52]]],
};

const saved = (version: number) => ({
  id: 'p1', name: 'Ext 19', rulesRef: 'eskom/0.1.0', authority: 'eskom', area: AREA,
  createdAt: '2026-10-03T10:00:00Z', updatedAt: '2026-10-03T10:00:00Z', version,
});

async function setup(id?: string) {
  localStorage.setItem(
    AUTH_STORAGE_KEY,
    JSON.stringify({ tokens: { accessToken: 'a', refreshToken: 'r', expiresAt: 0 }, user: { id: '1', email: 'x', displayName: 'X', registrationNo: null, roles: ['engineer'] } }),
  );
  TestBed.configureTestingModule({
    imports: [ProjectEdit],
    providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
  }).overrideComponent(ProjectEdit, { remove: { imports: [AreaMap, ProjectLayout] }, add: { imports: [AreaMapStub, ProjectLayoutStub] } });

  const fixture = TestBed.createComponent(ProjectEdit);
  if (id) fixture.componentRef.setInput('id', id);
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  http.expectOne('/api/system/rules').flush(['eskom/0.1.0']);
  if (id) http.expectOne(`/api/projects/${id}`).flush(saved(1));
  await fixture.whenStable();
  return { fixture, http, el: fixture.nativeElement as HTMLElement };
}

function fill(el: HTMLElement, name: string) {
  const input = el.querySelector<HTMLInputElement>('input[formcontrolname="name"]')!;
  input.value = name;
  input.dispatchEvent(new Event('input'));
  const select = el.querySelector<HTMLSelectElement>('select')!;
  select.value = 'eskom/0.1.0';
  select.dispatchEvent(new Event('change'));
}

describe('ProjectEdit', () => {
  afterEach(() => localStorage.clear());

  it('creates a project with the drawn area', async () => {
    const { fixture, http, el } = await setup();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fill(el, 'Ext 19');
    fixture.debugElement.query((d) => d.name === 'app-area-map').componentInstance.areaChange.emit(AREA);
    await fixture.whenStable();

    el.querySelector<HTMLButtonElement>('button[type="submit"]')!.click();
    const req = http.expectOne({ method: 'POST', url: '/api/projects' });
    expect(req.request.body).toEqual({ name: 'Ext 19', rulesRef: 'eskom/0.1.0', area: AREA, version: undefined });
    req.flush(saved(1));
    await fixture.whenStable();
    expect(nav).toHaveBeenCalledWith(['/projects', 'p1']);
  });

  it('shows server field errors', async () => {
    const { fixture, http, el } = await setup('p1');
    el.querySelector<HTMLButtonElement>('button[type="submit"]')!.click();
    http.expectOne({ method: 'PUT', url: '/api/projects/p1' }).flush(
      { title: 'One or more validation errors occurred.', errors: { area: ['Area must lie within South Africa.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();
    expect(el.textContent).toContain('Area must lie within South Africa.');
  });

  it('on 409 keeps the user edits and offers to load the latest', async () => {
    const { fixture, http, el } = await setup('p1');
    fill(el, 'My edit');
    el.querySelector<HTMLButtonElement>('button[type="submit"]')!.click();
    const put = http.expectOne({ method: 'PUT', url: '/api/projects/p1' });
    expect(put.request.body.version).toBe(1);
    put.flush({ title: 'Changed elsewhere' }, { status: 409, statusText: 'Conflict' });
    await fixture.whenStable();

    expect(el.textContent).toContain('changed elsewhere');
    expect(el.querySelector<HTMLInputElement>('input[formcontrolname="name"]')!.value).toBe('My edit');

    [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('load the latest'))!.click();
    http.expectOne('/api/projects/p1').flush({ ...saved(2), name: 'Their edit' });
    await fixture.whenStable();
    expect(el.querySelector<HTMLInputElement>('input[formcontrolname="name"]')!.value).toBe('Their edit');
    expect(el.textContent).not.toContain('changed elsewhere');
  });
});
