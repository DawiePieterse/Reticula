import { Component, input, output, signal } from '@angular/core';
import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BuildingPanel } from './building-panel';
import { FieldMap } from './field-map';
import { FieldPage } from './field-page';
import { GeolocationService } from './geolocation.service';
import { MAP_PACK_STORE, MemoryMapPackStore } from './map/map-pack.store';
import { answerRefresh, building, buildingField, flushSnapshot, settle, snapshot, syncTesting } from './sync/testing';

@Component({ selector: 'app-field-map', template: '' })
class FieldMapStub {
  readonly stands = input<unknown>();
  readonly network = input<unknown>();
  readonly buildings = input<unknown>();
  readonly candidates = input<unknown>();
  readonly selectedId = input<string | null>();
  readonly mode = input<string>();
  readonly gps = input<unknown>();
  readonly pack = input<unknown>();
  readonly buildingSelect = output<string>();
  readonly candidateSelect = output<string>();
  readonly mapTap = output<[number, number]>();
  readonly routeFinish = output<[number, number][]>();
}

@Component({ selector: 'app-building-panel', template: '<span class="panel-stub">{{ building()?.id }}</span>' })
class BuildingPanelStub {
  readonly building = input<{ id: string } | null>();
  readonly gps = input<unknown>();
  readonly form = input<unknown>();
  readonly load = input<unknown>();
  readonly openSync = output<void>();
}

const data = snapshot({
  buildings: {
    type: 'FeatureCollection',
    features: [building('a', { status: 'confirmed', confidence: 0.9, lowConfidence: false }), building('b', { confidence: 0.55 }), building('c', { confidence: 0.3 })],
  },
  assumptionsOpen: 4,
});

async function setup(online = true, savedMap = false) {
  const t = syncTesting(online);
  if (!online) await t.db.putSnapshot(data);
  const maps = new MemoryMapPackStore();
  if (savedMap) await maps.put({ projectId: 'p1', packId: 'm1', sizeBytes: 3, builtAt: '', source: 's', savedAt: '2026-10-05T07:00:00Z', bytes: new ArrayBuffer(3) });
  TestBed.configureTestingModule({
    imports: [FieldPage],
    providers: [
      ...t.providers, provideRouter([]), { provide: MAP_PACK_STORE, useValue: maps },
      { provide: GeolocationService, useValue: { fix: signal({ lon: 28.105, lat: -25.515, accuracyM: 5 }), error: signal(null), start: () => undefined, stop: () => undefined } },
    ],
  }).overrideComponent(FieldPage, { remove: { imports: [FieldMap, BuildingPanel] }, add: { imports: [FieldMapStub, BuildingPanelStub] } });
  const fixture = TestBed.createComponent(FieldPage);
  fixture.componentRef.setInput('id', 'p1');
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  await settle(2);
  if (online) {
    flushSnapshot(http, data);
    http.expectOne('/api/projects/p1/map-pack').flush({ pack: null, job: null });
  }
  await settle();
  await fixture.whenStable();
  const stable = async () => {
    await settle();
    await fixture.whenStable();
  };
  return { ...t, fixture, http, stable, el: fixture.nativeElement as HTMLElement };
}

const button = (el: HTMLElement, text: string) => [...el.querySelectorAll('button')].find((x) => x.textContent?.includes(text))!;

describe('FieldPage', () => {
  it('shows progress and starts with the lowest-confidence building', async () => {
    const { el, stable } = await setup();
    expect(el.textContent).toContain('1/3 inspected');
    expect(el.textContent).toContain('2 low-confidence left');
    expect(el.textContent).toContain('4 assumptions open');
    expect(el.textContent).toContain('All synced');
    expect(el.querySelector('header a')?.textContent).toContain('← Soshanguve');

    button(el, 'Start with lowest confidence').click();
    await stable();
    expect(el.querySelector('.panel-stub')?.textContent).toBe('c');

    button(el, 'Next to check').click();
    await stable();
    expect(el.querySelector('.panel-stub')?.textContent).toBe('b');
  });

  it('works offline from the data on the tablet and queues a new building', async () => {
    const { el, stable, db, online, http } = await setup(false);
    expect(el.textContent).toContain('Offline: working from the data saved on this tablet');
    expect(el.textContent).toContain('No offline map is saved');
    // Screens that need the server are not offered.
    expect(el.querySelector('header strong')?.textContent).toBe('Soshanguve');
    expect([...el.querySelectorAll('header a')].map((a) => a.textContent)).toEqual([]);
    expect(el.textContent).toContain('1/3 inspected');

    button(el, '+ Building').click();
    await stable();
    button(el, 'Use my position').click();
    await stable();
    button(el, 'shop').click();
    await stable();

    const [op] = await db.ops();
    expect(op.body).toMatchObject({ kind: 'addBuilding', req: { type: 'shop', position: { lon: 28.105, lat: -25.515, accuracyM: 5 } } });
    expect(el.querySelector('.panel-stub')?.textContent).toBe((op.body as { req: { id: string } }).req.id);
    expect(el.textContent).toContain('2/4 inspected');
    expect(el.textContent).toContain('1 on this tablet');

    // Back online: the queue is sent, then the project fetched again for what others changed.
    online.set(true);
    await stable();
    const id = (op.body as { req: { id: string } }).req.id;
    http.expectOne({ method: 'POST', url: '/api/projects/p1/buildings/new' }).flush(buildingField(id, { status: 'new', effectiveType: 'shop', erf: '7001', version: 1 }));
    await stable();
    const added = { ...building(id, { status: 'new', effectiveType: 'shop', erf: '7001' }), geometry: { type: 'Point' as const, coordinates: [28.105, -25.515] as [number, number] } };
    answerRefresh(http, { ...data, buildings: { ...data.buildings, features: [...data.buildings.features, added] }, assumptionsOpen: 5 });
    await stable();
    expect(el.textContent).toContain('All synced');
    expect(el.textContent).toContain('2/4 inspected');
    expect(el.textContent).toContain('5 assumptions open');
    expect(el.textContent).not.toContain('Offline');
  });

  it('draws the map saved on the tablet', async () => {
    const { fixture, el } = await setup(false, true);
    const map = fixture.debugElement.query((d) => d.name === 'app-field-map').componentInstance as FieldMapStub;
    expect((map.pack() as { packId: string }).packId).toBe('m1');
    expect(el.textContent).not.toContain('No offline map is saved');
    expect(el.textContent).toContain('Saved on this tablet 5 Oct');
  });

  it('places a transformer candidate where the map is tapped', async () => {
    const { fixture, el, stable, db } = await setup(false);
    button(el, '+ Transformer').click();
    await stable();
    fixture.debugElement.query((d) => d.name === 'app-field-map').componentInstance.mapTap.emit([28.106, -25.516]);
    await stable();
    expect((await db.ops())[0].body).toMatchObject({ kind: 'saveCandidate', req: { kind: 'transformer', geometry: { type: 'Point', coordinates: [28.106, -25.516] }, version: null } });
    expect(el.querySelector('aside h3')?.textContent).toContain('Transformer');
  });

  it('opens the sync panel with what needs a decision', async () => {
    const { el, stable, db, fixture } = await setup(false);
    await db.addOp({
      id: 'o1', projectId: 'p1', state: 'rejected', error: 'The position is outside the project area.', createdAt: '2026-10-05T08:00:00.000Z',
      body: { kind: 'addBuilding', req: { id: 'n1', inspectionId: 'i', type: 'house', position: { lon: 2.35, lat: 48.85, accuracyM: 3 }, capturedAt: '' } },
    });
    fixture.componentRef.setInput('id', 'p2');
    await stable();
    fixture.componentRef.setInput('id', 'p1');
    await stable();
    expect(button(el, '1 to decide')).toBeTruthy();
    button(el, '1 to decide').click();
    await stable();
    expect(el.textContent).toContain('The server refused this change: The position is outside the project area.');
    button(el, 'Discard').click();
    await stable();
    expect(await db.ops()).toEqual([]);
    expect(el.textContent).toContain('All synced');
  });
});
