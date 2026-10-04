import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ConnectivityService } from '../../core/connectivity.service';
import { Applied, FieldSync } from './field-sync';
import { BuildingField, LoadPoint } from './field.api';
import { idle } from '../../../testing/idle';

const building = (over: Partial<BuildingField> = {}): BuildingField => ({
  id: 'b1', status: 'confirmed', predictedType: 'house', confirmedType: 'house', effectiveType: 'house', confidence: 0.8, erf: '12',
  location: { type: 'Point', coordinates: [28.1, -25.5] }, inspectedAt: '2026-10-04T08:00:00Z', version: 7, ...over,
});
const selected = { id: 'b1', props: { status: 'predicted' as const, predictedType: 'house', confirmedType: null, erf: '12', confidence: 0.8 } };
const inspectReq = (version: number, action: 'confirm' | 'correct' | 'not_present' = 'confirm', type = 'house') => ({
  inspectionId: crypto.randomUUID(), action, type, position: null, capturedAt: '2026-10-04T09:00:00Z', notes: null, version,
});
const load = (over: Partial<LoadPoint> = {}): LoadPoint => ({
  id: 'lp1', buildingId: 'b1', kind: 'residential', specialLoad: null, observations: { dwelling: 'rdp' }, classOverride: null,
  incomeBand: 'b2', category: 'township_area', estimatedKva: 2.37, kva: 2.37, overridden: false, overrideReason: null, missing: [],
  status: 'estimated', updatedAt: '', version: 3, phases: 1, ...over,
});

function setup(online = true) {
  const net = signal(online);
  TestBed.configureTestingModule({
    providers: [provideHttpClient(), provideHttpClientTesting(), { provide: ConnectivityService, useValue: { online: net } }],
  });
  const sync = TestBed.inject(FieldSync);
  const http = TestBed.inject(HttpTestingController);
  const applied: Applied[] = [];
  sync.applied.subscribe((a) => applied.push(a));
  return { sync, http, net, applied };
}

describe('FieldSync', () => {
  it('sends straight to the server when online and nothing is waiting', async () => {
    const { sync, http } = setup();
    const result = sync.inspect('p1', selected, inspectReq(7));
    await idle();
    http.expectOne({ method: 'PUT', url: '/api/projects/p1/buildings/b1/inspection' }).flush(building({ version: 8 }));
    expect((await result).version).toBe(8);
    expect(sync.ops()).toEqual([]);
  });

  it('queues offline, answers at once, and sends in order on top of each new version when back online', async () => {
    const { sync, http, net, applied } = setup(false);
    const first = await sync.inspect('p1', selected, inspectReq(7, 'correct', 'shop'));
    expect(first).toMatchObject({ status: 'confirmed', effectiveType: 'shop', version: 7 });
    await sync.inspect('p1', selected, inspectReq(7, 'not_present'));
    expect(sync.pendingCount()).toBe(2);
    http.expectNone(() => true);

    net.set(true);
    TestBed.tick();
    await idle();
    const a = http.expectOne('/api/projects/p1/buildings/b1/inspection');
    expect(a.request.body).toMatchObject({ action: 'correct', type: 'shop', version: 7 });
    a.flush(building({ confirmedType: 'shop', effectiveType: 'shop', version: 8 }));
    await idle();
    const b = http.expectOne('/api/projects/p1/buildings/b1/inspection');
    expect(b.request.body).toMatchObject({ action: 'not_present', version: 8 });
    b.flush(building({ status: 'notpresent', confirmedType: null, version: 9 }));
    await idle();
    expect(sync.ops()).toEqual([]);
    expect(applied.map((x) => (x.entity as BuildingField).version)).toEqual([8, 9]);
    expect(sync.lastSync()).not.toBeNull();
  });

  it('keeps the outbox on the device across restarts', async () => {
    const { sync } = setup(false);
    await sync.addBuilding('p1', { id: 'n1', inspectionId: 'i1', type: 'shop', position: { lon: 28.1, lat: -25.5, accuracyM: 4 }, capturedAt: '2026-10-04T09:00:00Z' });
    TestBed.resetTestingModule();
    const again = setup(false);
    await idle();
    expect(again.sync.ops().map((o) => [o.kind, o.targetId])).toEqual([['addBuilding', 'n1']]);
  });

  it('parks a conflict for the inspector and never overwrites; keep mine resends on the server version', async () => {
    const { sync, http, net } = setup(false);
    await sync.saveLoad('p1', 'b1', { kind: 'residential', observations: { dwelling: 'brick_large' }, version: 3 }, load(), 'erf 12');
    net.set(true);
    TestBed.tick();
    await idle();
    http.expectOne('/api/projects/p1/buildings/b1/load').flush(load({ observations: { dwelling: 'informal' }, version: 5 }), { status: 409, statusText: 'Conflict' });
    await idle();
    const [op] = sync.problems();
    expect(op.state).toBe('conflict');
    expect(sync.compare(op).find((r) => r.field === 'dwelling')).toEqual({ field: 'dwelling', mine: 'brick large', theirs: 'informal', differs: true });

    void sync.keepMine(op);
    await idle();
    const resend = http.expectOne('/api/projects/p1/buildings/b1/load');
    expect(resend.request.body).toMatchObject({ observations: { dwelling: 'brick_large' }, version: 5 });
    resend.flush(load({ observations: { dwelling: 'brick_large' }, version: 6 }));
    await idle();
    expect(sync.ops()).toEqual([]);
  });

  it('keep the server\'s drops my change and hands the server copy to the screen', async () => {
    const { sync, http, net, applied } = setup(false);
    await sync.inspect('p1', selected, inspectReq(7, 'not_present'));
    net.set(true);
    TestBed.tick();
    await idle();
    http.expectOne('/api/projects/p1/buildings/b1/inspection').flush(building({ version: 9 }), { status: 409, statusText: 'Conflict' });
    await idle();
    await sync.keepTheirs(sync.problems()[0]);
    expect(sync.ops()).toEqual([]);
    expect(applied.at(-1)).toMatchObject({ kind: 'inspect', entity: { version: 9 } });
  });

  it('treats a 409 that already matches my change as done (the reply was lost)', async () => {
    const { sync, http, net } = setup(false);
    await sync.saveLoad('p1', 'b1', { kind: 'residential', observations: { dwelling: 'rdp' }, version: null }, null, 'erf 12');
    net.set(true);
    TestBed.tick();
    await idle();
    http.expectOne('/api/projects/p1/buildings/b1/load').flush(load(), { status: 409, statusText: 'Conflict' });
    await idle();
    expect(sync.ops()).toEqual([]);
  });

  it('holds back later changes to the same thing behind a conflict but sends the rest', async () => {
    const { sync, http, net } = setup(false);
    await sync.inspect('p1', selected, inspectReq(7));
    await sync.inspect('p1', selected, inspectReq(7, 'correct', 'shop'));
    await sync.saveCandidate('p1', 'c1', { kind: 'pole', geometry: { type: 'Point', coordinates: [28.1, -25.5] } });
    net.set(true);
    TestBed.tick();
    await idle();
    http.expectOne('/api/projects/p1/buildings/b1/inspection').flush(building({ version: 9 }), { status: 409, statusText: 'Conflict' });
    await idle();
    const c = http.expectOne('/api/projects/p1/candidates/c1');
    c.flush({ type: 'Feature', id: 'c1', geometry: c.request.body.geometry, properties: { kind: 'pole', notes: null, createdAt: '', version: 1 } });
    await idle();
    http.expectNone('/api/projects/p1/buildings/b1/inspection');
    expect(sync.ops().map((o) => [o.kind, o.state])).toEqual([['inspect', 'conflict'], ['inspect', 'pending']]);
  });

  it('marks a change the server rejects as invalid, and lets the inspector discard it', async () => {
    const { sync, http, net } = setup(false);
    await sync.saveLoad('p1', 'b1', { kind: 'special', specialLoad: 'school', version: null }, null, 'erf 12');
    net.set(true);
    TestBed.tick();
    await idle();
    http.expectOne('/api/projects/p1/buildings/b1/load').flush({ errors: { building: ['The building was marked not present.'] } }, { status: 400, statusText: 'Bad Request' });
    await idle();
    const [op] = sync.problems();
    expect(op).toMatchObject({ state: 'failed', error: 'The building was marked not present.' });
    await sync.discard(op);
    expect(sync.ops()).toEqual([]);
  });

  it('stops quietly when the connection drops mid-sync and keeps the change', async () => {
    const { sync, http, net } = setup(false);
    await sync.archiveCandidate('p1', 'c1', 'pole');
    net.set(true);
    TestBed.tick();
    await idle();
    http.expectOne('/api/projects/p1/candidates/c1').error(new ProgressEvent('error'), { status: 0 });
    await idle();
    expect(sync.ops().map((o) => o.state)).toEqual(['pending']);
  });

  it('keeps an offline photo and uploads it later', async () => {
    const { sync, http, net } = setup(false);
    await sync.uploadPhoto('p1', { id: 'ph1', blob: new Blob([new Uint8Array([1, 2, 3])], { type: 'image/jpeg' }), buildingId: 'b1', capturedAt: '2026-10-04T09:00:00Z' });
    expect(sync.ops()[0]).toMatchObject({ kind: 'photo', request: { id: 'ph1', buildingId: 'b1' } });
    net.set(true);
    TestBed.tick();
    await idle();
    const req = http.expectOne({ method: 'POST', url: '/api/projects/p1/photos' });
    const form = req.request.body as FormData;
    expect(form.get('id')).toBe('ph1');
    expect(form.get('buildingId')).toBe('b1');
    req.flush({ id: 'ph1' });
    await idle();
    expect(sync.ops()).toEqual([]);
  });

  it('queues a load offline without a calculated kVA', async () => {
    const { sync } = setup(false);
    const lp = await sync.saveLoad('p1', 'b1', { kind: 'residential', observations: { dwelling: 'rdp' }, loadClass: 'township_area', version: null }, null, 'erf 12');
    expect(lp).toMatchObject({ pendingSync: true, classOverride: 'township_area', kva: 0 });
  });

  it('sends a load only after the building\'s own changes are settled', async () => {
    const { sync, http, net } = setup(false);
    await sync.inspect('p1', selected, inspectReq(7, 'correct', 'shop'));
    await sync.saveLoad('p1', 'b1', { kind: 'special', specialLoad: 'shop', version: null }, null, 'erf 12');
    net.set(true);
    TestBed.tick();
    await idle();
    http.expectOne('/api/projects/p1/buildings/b1/inspection').flush(building({ status: 'notpresent', version: 9 }), { status: 409, statusText: 'Conflict' });
    await idle();
    http.expectNone('/api/projects/p1/buildings/b1/load');

    void sync.keepMine(sync.problems()[0]);
    await idle();
    http.expectOne('/api/projects/p1/buildings/b1/inspection').flush(building({ effectiveType: 'shop', version: 10 }));
    await idle();
    http.expectOne('/api/projects/p1/buildings/b1/load').flush(load({ kind: 'special', specialLoad: 'shop' }));
    await idle();
    expect(sync.ops()).toEqual([]);
  });
});
