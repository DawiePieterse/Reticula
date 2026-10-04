import { Component, input, output } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { BuildingPanel } from './building-panel';
import { FieldMap } from './field-map';
import { FieldPage } from './field-page';
import { GeolocationService } from './geolocation.service';
import { signal } from '@angular/core';
import { ConnectivityService } from '../../core/connectivity.service';
import { localProgress } from './field-page';
import { idle } from '../../../testing/idle';

@Component({ selector: 'app-field-map', template: '' })
class FieldMapStub {
  readonly stands = input<unknown>();
  readonly buildings = input<unknown>();
  readonly candidates = input<unknown>();
  readonly selectedId = input<string | null>();
  readonly mode = input<string>();
  readonly gps = input<unknown>();
  readonly buildingSelect = output<string>();
  readonly candidateSelect = output<string>();
  readonly mapTap = output<[number, number]>();
  readonly routeFinish = output<[number, number][]>();
}

@Component({ selector: 'app-building-panel', template: '<span class="panel-stub">{{ building()?.id }}</span>' })
class BuildingPanelStub {
  readonly projectId = input<string>();
  readonly building = input<{ id: string } | null>();
  readonly gps = input<unknown>();
  readonly form = input<unknown>();
  readonly load = input<unknown>();
  readonly changed = output<unknown>();
  readonly loadSaved = output<unknown>();
}

const square = { type: 'Polygon', coordinates: [[[28.1, -25.52], [28.11, -25.52], [28.11, -25.51], [28.1, -25.52]]] };
const b = (id: string, confidence: number, status = 'predicted') => ({
  type: 'Feature', id, geometry: square,
  properties: { predictedType: 'house', confidence, source: 's', lowConfidence: confidence < 0.6, status, confirmedType: null, effectiveType: 'house', areaM2: 50, erf: id, zoning: null, signals: [], version: 1 },
});
const progress = { buildings: 3, confirmed: 1, notPresent: 0, added: 0, outstanding: 2, outstandingLowConfidence: 2, loadsEstimated: 0, loadsConfirmed: 0, buildingsWithoutLoad: 3, assumptionsOpen: 4, candidates: {} };

async function setup() {
  TestBed.configureTestingModule({
    imports: [FieldPage],
    providers: [
      provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
      { provide: GeolocationService, useValue: { fix: signal({ lon: 28.105, lat: -25.515, accuracyM: 5 }), error: signal(null), start: () => undefined, stop: () => undefined } },
    ],
  }).overrideComponent(FieldPage, { remove: { imports: [FieldMap, BuildingPanel] }, add: { imports: [FieldMapStub, BuildingPanelStub] } });
  const fixture = TestBed.createComponent(FieldPage);
  fixture.componentRef.setInput('id', 'p1');
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  await idle();
  http.expectOne('/api/projects/p1').flush({ id: 'p1', name: 'Soshanguve', rulesRef: 'eskom/0.1.0', authority: 'eskom', area: square, createdAt: '', updatedAt: '', version: 1 });
  await idle();
  http.expectOne('/api/projects/p1/stands').flush({ type: 'FeatureCollection', features: [] });
  await idle();
  http.expectOne('/api/projects/p1/buildings').flush({ type: 'FeatureCollection', features: [b('a', 0.9, 'confirmed'), b('b', 0.55), b('c', 0.3)] });
  await idle();
  http.expectOne('/api/projects/p1/candidates').flush({ type: 'FeatureCollection', features: [] });
  await idle();
  http.expectOne('/api/projects/p1/load-points').flush([]);
  await idle();
  http.expectOne('/api/projects/p1/admd-form').flush({ rules_hash: 'x', indicators: [], multi_indicators: [], band_indicators: [], special_loads: {} });
  await idle();
  http.expectOne('/api/projects/p1/field-progress').flush(progress);
  await new Promise((r) => setTimeout(r));
  await fixture.whenStable();
  return { fixture, http, el: fixture.nativeElement as HTMLElement };
}

const settle = async (f: { whenStable(): Promise<unknown> }) => {
  await new Promise((r) => setTimeout(r));
  await f.whenStable();
};
const button = (el: HTMLElement, text: string) => [...el.querySelectorAll('button')].find((x) => x.textContent?.includes(text))!;

describe('FieldPage', () => {
  it('shows progress and starts with the lowest-confidence building', async () => {
    const { fixture, el } = await setup();
    expect(el.textContent).toContain('1/3 inspected');
    expect(el.textContent).toContain('2 low-confidence left');
    expect(el.textContent).toContain('4 assumptions open');

    button(el, 'Start with lowest confidence').click();
    await settle(fixture);
    expect(el.querySelector('.panel-stub')?.textContent).toBe('c');

    button(el, 'Next to check').click();
    await settle(fixture);
    expect(el.querySelector('.panel-stub')?.textContent).toBe('b');
  });

  it('adds a new building at the GPS position', async () => {
    const { fixture, http, el } = await setup();
    button(el, '+ Building').click();
    await settle(fixture);
    button(el, 'Use my position').click();
    await settle(fixture);
    button(el, 'shop').click();
    await idle();
    const req = http.expectOne({ method: 'POST', url: '/api/projects/p1/buildings/new' });
    expect(req.request.body).toMatchObject({ type: 'shop', position: { lon: 28.105, lat: -25.515, accuracyM: 5 } });
    req.flush({ id: 'n1', status: 'new', predictedType: 'shop', confirmedType: 'shop', effectiveType: 'shop', confidence: 1, erf: null, location: { type: 'Point', coordinates: [28.105, -25.515] }, inspectedAt: '', version: 1 });
    await settle(fixture);
    await idle();
    http.expectOne('/api/projects/p1/field-progress').flush({ ...progress, buildings: 4, added: 1 });
    await settle(fixture);
    expect(el.querySelector('.panel-stub')?.textContent).toBe('n1');
    expect(el.textContent).toContain('2/4 inspected');
  });

  it('places a transformer candidate where the map is tapped', async () => {
    const { fixture, http, el } = await setup();
    button(el, '+ Transformer').click();
    await settle(fixture);
    fixture.debugElement.query((d) => d.name === 'app-field-map').componentInstance.mapTap.emit([28.106, -25.516]);
    await idle();
    const req = http.expectOne((r) => r.method === 'PUT' && r.url.startsWith('/api/projects/p1/candidates/'));
    expect(req.request.body).toMatchObject({ kind: 'transformer', geometry: { type: 'Point', coordinates: [28.106, -25.516] } });
    req.flush({ type: 'Feature', id: 'c1', geometry: { type: 'Point', coordinates: [28.106, -25.516] }, properties: { kind: 'transformer', notes: null, createdAt: '', version: 1 } });
    await settle(fixture);
    await idle();
    http.expectOne('/api/projects/p1/field-progress').flush(progress);
    await settle(fixture);
    expect(el.querySelector('aside h3')?.textContent).toContain('Transformer');
  });

  it('opens offline from the copy kept on the tablet and queues changes', async () => {
    await setup();
    await new Promise((r) => setTimeout(r, 500)); // the copy is written shortly after the data arrives
    TestBed.resetTestingModule();

    TestBed.configureTestingModule({
      imports: [FieldPage],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: ConnectivityService, useValue: { online: signal(false) } },
        { provide: GeolocationService, useValue: { fix: signal({ lon: 28.105, lat: -25.515, accuracyM: 5 }), error: signal(null), start: () => undefined, stop: () => undefined } },
      ],
    }).overrideComponent(FieldPage, { remove: { imports: [FieldMap, BuildingPanel] }, add: { imports: [FieldMapStub, BuildingPanelStub] } });
    const fixture = TestBed.createComponent(FieldPage);
    fixture.componentRef.setInput('id', 'p1');
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await idle();
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    http.expectNone(() => true);
    expect(el.textContent).toContain('Soshanguve');
    expect(el.textContent).toContain('1/3 inspected');
    expect(el.textContent).toContain('Offline · 0 waiting');
    expect(el.textContent).not.toContain('Loads');

    button(el, '+ Building').click();
    await settle(fixture);
    button(el, 'Use my position').click();
    await settle(fixture);
    button(el, 'shop').click();
    await idle();
    await settle(fixture);
    http.expectNone(() => true);
    expect(el.querySelector('.panel-stub')?.textContent).toBeTruthy();
    expect(el.textContent).toContain('2/4 inspected');
    expect(el.textContent).toContain('Offline · 1 waiting');
  });

  it('says so when a project was never opened online on this tablet', async () => {
    TestBed.configureTestingModule({
      imports: [FieldPage],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: ConnectivityService, useValue: { online: signal(false) } },
        { provide: GeolocationService, useValue: { fix: signal(null), error: signal(null), start: () => undefined, stop: () => undefined } },
      ],
    }).overrideComponent(FieldPage, { remove: { imports: [FieldMap, BuildingPanel] }, add: { imports: [FieldMapStub, BuildingPanelStub] } });
    const fixture = TestBed.createComponent(FieldPage);
    fixture.componentRef.setInput('id', 'p9');
    fixture.detectChanges();
    await idle();
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('not available offline');
  });

  it('counts progress on the tablet, keeping the server\'s open-assumption figure', () => {
    const fc = { type: 'FeatureCollection' as const, features: [b('a', 0.9, 'confirmed'), b('b', 0.55), b('c', 0.3, 'notpresent'), b('d', 1, 'new')] };
    const loads = new Map([['a', { status: 'estimated' }], ['d', { status: 'confirmed' }]]) as never;
    const p = localProgress(fc as never, loads, { type: 'FeatureCollection', features: [{ type: 'Feature', id: 'x', geometry: { type: 'Point', coordinates: [0, 0] }, properties: { kind: 'pole', notes: null, createdAt: '', version: 1 } }] }, progress);
    expect(p).toEqual({
      buildings: 4, confirmed: 1, notPresent: 1, added: 1, outstanding: 1, outstandingLowConfidence: 1,
      loadsEstimated: 1, loadsConfirmed: 1, buildingsWithoutLoad: 1, assumptionsOpen: 4, candidates: { pole: 1 },
    });
  });
});
