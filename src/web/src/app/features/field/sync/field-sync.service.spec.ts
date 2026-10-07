import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { FieldSync, NotOnDevice } from './field-sync.service';
import { buildingKey, compare, loadKey } from './outbox';
import { answerRefresh, buildingField, flushSnapshot, loadPoint, settle, snapshot, syncTesting } from './testing';

const base = '/api/projects/p1';
let controller: HttpTestingController | null = null;

function inject() {
  const sync = TestBed.inject(FieldSync);
  const http = (controller = TestBed.inject(HttpTestingController));
  return { sync, http };
}

/** A sync service with project p1 on the device, opened offline. */
async function offline(s = snapshot()) {
  const t = syncTesting(false);
  await t.db.putSnapshot(s);
  TestBed.configureTestingModule({ providers: t.providers });
  const { sync, http } = inject();
  await sync.open('p1');
  return { ...t, sync, http, goOnline: async () => { t.online.set(true); await settle(); } };
}

describe('FieldSync', () => {
  afterEach(() => {
    try {
      // The open-assumptions count after each sync, and the refresh after reconnecting; tests that care answer them.
      if (controller) answerRefresh(controller);
      controller?.verify();
    } finally {
      controller = null;
      TestBed.resetTestingModule();
    }
  });

  it('opens a project from the server and keeps it for offline use', async () => {
    const t = syncTesting(true);
    TestBed.configureTestingModule({ providers: t.providers });
    const { sync, http } = inject();
    const opened = sync.open('p1');
    await settle(2);
    flushSnapshot(http, snapshot({ photoCounts: { b1: 2 } }));
    await opened;

    expect(sync.view()!.buildings.features.length).toBe(3);
    expect(sync.view()!.photoCounts['b1']).toBe(2);
    expect((await t.db.snapshot('p1'))!.projectName).toBe('Soshanguve');
    expect((await sync.cachedProjects()).map((p) => p.projectName)).toEqual(['Soshanguve']);

    // Later, with no connection: the same data, no requests.
    t.online.set(false);
    await sync.open('p1');
    expect(sync.view()!.buildings.features.length).toBe(3);
  });

  it('cannot open offline a project never opened on the device', async () => {
    const t = syncTesting(false);
    TestBed.configureTestingModule({ providers: t.providers });
    await expect(TestBed.inject(FieldSync).open('p9')).rejects.toBeInstanceOf(NotOnDevice);
  });

  it('shows changes made offline at once and sends them in order when back online', async () => {
    const { sync, http, goOnline, db } = await offline();
    await sync.inspect('b1', 'confirm', 'house', { lon: 28.1, lat: -25.52, accuracyM: 4 }, 'Shack at the back');
    await sync.saveLoad('b1', { kind: 'residential', observations: { dwelling: 'informal' } });
    const added = await sync.addBuilding('shop', { lon: 28.1002, lat: -25.5201, accuracyM: 3 }, null);

    const v = sync.view()!;
    expect(v.buildings.features.find((f) => f.id === 'b1')!.properties.status).toBe('confirmed');
    expect(v.buildings.features.find((f) => f.id === added)!.properties.status).toBe('new');
    expect(v.loads.get('b1')!.pending).toBe(true);
    expect(v.unsynced.has(buildingKey('b1'))).toBe(true);
    expect(v.progress).toMatchObject({ buildings: 4, confirmed: 2, added: 1, loadsEstimated: 1, buildingsWithoutLoad: 3 });
    expect(sync.queued()).toBe(3);

    await goOnline();
    const inspect = http.expectOne({ method: 'PUT', url: `${base}/buildings/b1/inspection` });
    expect(inspect.request.body).toMatchObject({ action: 'confirm', type: 'house', version: 1, notes: 'Shack at the back', position: { accuracyM: 4 } });
    inspect.flush(buildingField('b1', { version: 2 }));
    await settle();

    const load = http.expectOne({ method: 'PUT', url: `${base}/buildings/b1/load` });
    const loadOp = (await db.ops())[0];
    expect(load.request.body).toMatchObject({ kind: 'residential', observations: { dwelling: 'informal' }, version: null, opId: loadOp.id });
    load.flush(loadPoint('b1', { kva: 1.3, version: 1 }));
    await settle();

    http.expectOne({ method: 'POST', url: `${base}/buildings/new` }).flush(buildingField(added, { status: 'new', effectiveType: 'shop', erf: '7001', version: 1 }));
    await settle();
    http.match(`${base}/field-progress`).forEach((r) => r.flush({ assumptionsOpen: 5 }));
    await settle();

    expect(await db.ops()).toEqual([]);
    expect(sync.queued()).toBe(0);
    const after = sync.view()!;
    expect(after.loads.get('b1')!.kva).toBe(1.3);
    expect(after.loads.get('b1')!.pending).toBeUndefined();
    expect(after.buildings.features.find((f) => f.id === added)!.properties.erf).toBe('7001');
    expect(after.progress.assumptionsOpen).toBe(5);
    expect((await db.snapshot('p1'))!.buildings.features.find((f) => f.id === 'b1')!.properties.version).toBe(2);
  });

  it('sends a change made on top of an unsynced one with the version the server gave the first', async () => {
    const { sync, http, goOnline } = await offline();
    await sync.inspect('b1', 'correct', 'shop', null, null);
    await sync.inspect('b1', 'correct', 'school', null, null);
    expect(sync.view()!.buildings.features[0].properties.effectiveType).toBe('school');

    await goOnline();
    const first = http.expectOne(`${base}/buildings/b1/inspection`);
    expect(first.request.body).toMatchObject({ type: 'shop', version: 1 });
    first.flush(buildingField('b1', { effectiveType: 'shop', confirmedType: 'shop', version: 7 }));
    await settle();

    // Made while the first was still queued: it goes against the first's result, not a fresh fetch.
    const second = http.expectOne(`${base}/buildings/b1/inspection`);
    expect(second.request.body).toMatchObject({ type: 'school', version: 7 });
    second.flush(buildingField('b1', { effectiveType: 'school', confirmedType: 'school', version: 8 }));
    await settle();
  });

  it('chains a change made while the earlier one is in flight', async () => {
    const { sync, http, goOnline } = await offline();
    await goOnline();
    await sync.inspect('b1', 'correct', 'shop', null, null);
    await settle();
    const first = http.expectOne(`${base}/buildings/b1/inspection`);
    await sync.inspect('b1', 'not_present', undefined, null, null);
    first.flush(buildingField('b1', { effectiveType: 'shop', version: 9 }));
    await settle();
    expect(http.expectOne(`${base}/buildings/b1/inspection`).request.body).toMatchObject({ action: 'not_present', version: 9 });
  });

  it('holds a change to an item that changed on the server, never overwriting it, and lets the person keep theirs or the server\'s', async () => {
    const { sync, http, goOnline, db } = await offline();
    await sync.inspect('b1', 'confirm', 'house', null, null);
    await sync.inspect('b2', 'not_present', undefined, null, null);

    await goOnline();
    http.expectOne(`${base}/buildings/b1/inspection`).flush(
      buildingField('b1', { effectiveType: 'shop', confirmedType: 'shop', version: 3 }), { status: 409, statusText: 'Conflict' });
    await settle();
    // Other buildings are not held up.
    http.expectOne(`${base}/buildings/b2/inspection`).flush(buildingField('b2', { status: 'notpresent', confirmedType: null, version: 2 }));
    await settle();
    await settle();

    // The server's version shows; the change waits with both side by side.
    const v = sync.view()!;
    expect(v.buildings.features[0].properties.effectiveType).toBe('shop');
    const held = v.issues.get(buildingKey('b1'))!;
    expect(held.state).toBe('conflict');
    expect(compare(held).find((r) => r.label === 'Type')).toEqual({ label: 'Type', mine: 'house', theirs: 'shop', differs: true });
    expect(sync.needsDecision()).toBe(1);

    await sync.keepMine(held);
    await settle();
    expect(http.expectOne(`${base}/buildings/b1/inspection`).request.body).toMatchObject({ type: 'house', version: 3 });
    await settle(1);
    expect((await db.ops())[0].state).toBe('pending');
  });

  it('drops a held change when the server\'s version is kept', async () => {
    const { sync, http, goOnline, db } = await offline();
    await sync.inspect('b1', 'confirm', 'house', null, null);
    await goOnline();
    http.expectOne(`${base}/buildings/b1/inspection`).flush(buildingField('b1', { effectiveType: 'shop', version: 3 }), { status: 409, statusText: 'Conflict' });
    await settle();

    await sync.keepTheirs(sync.issues()[0]);
    await settle();
    expect(await db.ops()).toEqual([]);
    expect(sync.view()!.buildings.features[0].properties.effectiveType).toBe('shop');
    expect(sync.needsDecision()).toBe(0);
  });

  it('holds a later change to the same item, and asks again when the change it was made on is dropped', async () => {
    const { sync, http, goOnline } = await offline(snapshot({ loads: [loadPoint('b1', { version: 3 })] }));
    await sync.saveLoad('b1', { kind: 'special', specialLoad: 'school' });
    await sync.saveLoad('b1', { kind: 'special', specialLoad: 'shop' });

    await goOnline();
    const first = http.expectOne(`${base}/buildings/b1/load`);
    expect(first.request.body).toMatchObject({ version: 3 });
    first.flush(loadPoint('b1', { kva: 2.4, status: 'confirmed', version: 6 }), { status: 409, statusText: 'Conflict' });
    await settle();
    // The second is not sent while the first waits.
    expect(sync.issues().length).toBe(1);
    expect(sync.view()!.loads.get('b1')).toMatchObject({ pending: true, specialLoad: 'shop' });

    await sync.keepTheirs(sync.issues()[0]);
    await settle();
    // Made on top of a dropped change: held against the server's state, not sent.
    const held = sync.view()!.issues.get(loadKey('b1'))!;
    expect(held.state).toBe('conflict');
    expect(held.server).toMatchObject({ kva: 2.4, version: 6 });
    expect(compare(held).find((r) => r.label === 'Status')!.theirs).toBe('Confirmed by the engineer');
  });

  it('keeps a refused change with the reason, and discarding a refused new building drops the changes made to it', async () => {
    const { sync, http, goOnline, db } = await offline();
    const id = await sync.addBuilding('house', { lon: 2.35, lat: 48.85, accuracyM: 5 }, null);
    await sync.inspect(id, 'correct', 'shop', null, null);
    await sync.addPhoto({ buildingId: id }, new Blob([new Uint8Array([0xff, 0xd8])], { type: 'image/jpeg' }));
    await sync.inspect('b1', 'confirm', 'house', null, null);

    await goOnline();
    http.expectOne(`${base}/buildings/new`).flush(
      { title: 'One or more validation errors occurred.', errors: { position: ['The position is outside the project area.'] } },
      { status: 400, statusText: 'Bad Request' });
    await settle();
    http.expectOne(`${base}/buildings/b1/inspection`).flush(buildingField('b1'));
    await settle();
    await settle();

    const refused = sync.issues()[0];
    expect(refused).toMatchObject({ state: 'rejected', error: 'The position is outside the project area.' });
    expect(sync.dependents(refused).length).toBe(2);
    await sync.discard(refused);
    await settle();
    expect(await db.ops()).toEqual([]);
    expect(sync.view()!.buildings.features.some((f) => f.id === id)).toBe(false);
  });

  it('keeps changes queued while the server cannot be reached', async () => {
    const { sync, http, goOnline, db } = await offline();
    await sync.inspect('b1', 'confirm', 'house', null, null);
    await goOnline();
    http.expectOne(`${base}/buildings/b1/inspection`).error(new ProgressEvent('error'), { status: 0 });
    await settle();
    expect(sync.lastError()).toContain('Cannot reach the server');
    expect((await db.ops())[0].state).toBe('pending');

    void sync.syncNow();
    await settle();
    http.expectOne(`${base}/buildings/b1/inspection`).flush(buildingField('b1'));
    await settle();
    await settle();
    expect(await db.ops()).toEqual([]);
    expect(sync.lastError()).toBeNull();
  });

  it('uploads queued photos with their device ids', async () => {
    const { sync, http, goOnline } = await offline();
    await sync.addPhoto({ buildingId: 'b2' }, new Blob([new Uint8Array([0xff, 0xd8])], { type: 'image/jpeg' }));
    expect(sync.view()!.photoCounts['b2']).toBe(1);
    await goOnline();
    const req = http.expectOne({ method: 'POST', url: `${base}/photos` });
    const form = req.request.body as FormData;
    expect(form.get('buildingId')).toBe('b2');
    expect(form.get('id')).toMatch(/^[0-9a-f-]{36}$/);
    req.flush({ id: form.get('id'), buildingId: 'b2' });
    await settle();
    await settle();
    expect(sync.view()!.photoCounts['b2']).toBe(1);
  });

  it('adds, edits and removes candidates, sending edits against the version the create returned', async () => {
    const { sync, http, goOnline } = await offline();
    const id = await sync.saveCandidate(null, 'transformer', { type: 'Point', coordinates: [28.1, -25.52] }, null, null);
    await sync.saveCandidate(id, 'transformer', { type: 'Point', coordinates: [28.1, -25.52] }, 'Next to the tap', null);
    expect(sync.view()!.candidates.features[0].properties.notes).toBe('Next to the tap');
    await sync.archiveCandidate(id);
    expect(sync.view()!.candidates.features).toEqual([]);

    await goOnline();
    const create = http.expectOne(`${base}/candidates/${id}`);
    expect(create.request.body).toMatchObject({ version: null, opId: expect.any(String) });
    create.flush({ type: 'Feature', id, geometry: { type: 'Point', coordinates: [28.1, -25.52] }, properties: { kind: 'transformer', notes: null, createdAt: '', version: 11, source: 'field' } });
    await settle();
    const edit = http.expectOne(`${base}/candidates/${id}`);
    expect(edit.request.body).toMatchObject({ notes: 'Next to the tap', version: 11 });
    edit.flush({ type: 'Feature', id, geometry: { type: 'Point', coordinates: [28.1, -25.52] }, properties: { kind: 'transformer', notes: 'Next to the tap', createdAt: '', version: 12 } });
    await settle();
    // Already removed on the server counts as done.
    http.expectOne({ method: 'DELETE', url: `${base}/candidates/${id}` }).flush(null, { status: 404, statusText: 'Not Found' });
    await settle();
    await settle();
    expect(sync.queued()).toBe(0);
    expect(sync.view()!.candidates.features).toEqual([]);
  });
});
