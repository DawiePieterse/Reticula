import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { Job, JobsService } from '../../../core/jobs/jobs.service';
import { settle, syncTesting } from '../sync/testing';
import { MAP_PACK_STORE, MemoryMapPackStore } from './map-pack.store';
import { MapPackInfo, MapPacks } from './map-packs.service';

const pack = (over: Partial<MapPackInfo> = {}): MapPackInfo => ({
  id: 'm1', sizeBytes: 4, sha256: 'x', bbox: [28.09, -25.53, 28.11, -25.51], maxZoom: 15, tileCount: 40, source: 'planet.pmtiles',
  builtAt: '2026-10-05T07:00:00Z', ...over,
});
const job = (status: Job['status'], over: Partial<Job> = {}): Job => ({
  id: 'j1', kind: 'map.pack', status, progressPct: status === 'running' ? 10 : 100, message: null, error: null, startedAt: null, finishedAt: null, ...over,
});

function setup(online = true) {
  const t = syncTesting(online);
  const store = new MemoryMapPackStore();
  const updates = new Subject<Job>();
  const polled = new Subject<Job>();
  TestBed.configureTestingModule({
    providers: [...t.providers, { provide: MAP_PACK_STORE, useValue: store }, { provide: JobsService, useValue: { watch: () => updates, get: () => polled } }],
  });
  return { ...t, store, updates, polled, maps: TestBed.inject(MapPacks), http: TestBed.inject(HttpTestingController) };
}

const url = '/api/projects/p1/map-pack';

describe('MapPacks', () => {
  it('prepares the map on the server, follows the build and saves the pack on the tablet', async () => {
    const { maps, http, updates, store } = setup();
    const opened = maps.open('p1');
    await settle(1);
    http.expectOne(url).flush({ pack: null, job: null });
    await opened;
    expect(maps.server()).toEqual({ pack: null, job: null });

    const built = maps.build();
    await settle(1);
    http.expectOne({ method: 'POST', url }).flush(job('queued'));
    await settle(1);
    updates.next(job('running', { message: 'Cutting the map for the project area' }));
    expect(maps.building()?.message).toBe('Cutting the map for the project area');
    updates.next(job('succeeded'));
    updates.complete();
    await settle(1);
    http.expectOne({ method: 'GET', url }).flush({ pack: pack(), job: null });
    await settle(1);
    http.expectOne(`${url}/m1.pmtiles`).flush(new Uint8Array([0x50, 0x4d, 0x54, 0x69]).buffer);
    await built;

    expect(maps.building()).toBeNull();
    expect(maps.local()).toMatchObject({ projectId: 'p1', packId: 'm1', sizeBytes: 4, source: 'planet.pmtiles' });
    expect((await store.get('p1'))!.bytes.byteLength).toBe(4);
    expect(maps.outdated()).toBe(false);
    http.verify();
  });

  it('reports a failed build with the reason from the server', async () => {
    const { maps, http, updates } = setup();
    const opened = maps.open('p1');
    await settle(1);
    http.expectOne(url).flush({ pack: null, job: null });
    await opened;
    const built = maps.build();
    await settle(1);
    http.expectOne({ method: 'POST', url }).flush(job('queued'));
    await settle(1);
    updates.next(job('failed', { error: 'No offline map source is configured.' }));
    updates.complete();
    await settle(1);
    http.expectOne(url).flush({ pack: null, job: job('failed', { error: 'No offline map source is configured.' }) });
    await built;
    expect(maps.error()).toBe('No offline map source is configured.');
    expect(maps.local()).toBeNull();
    http.verify();
  });

  it('uses the pack on the tablet offline and notices a newer one online', async () => {
    const { maps, http, store, online } = setup(false);
    await store.put({ projectId: 'p1', packId: 'm1', sizeBytes: 4, builtAt: '', source: 's', savedAt: '', bytes: new ArrayBuffer(4) });
    await maps.open('p1');
    expect(maps.local()?.packId).toBe('m1');
    http.verify();

    online.set(true);
    const refreshed = maps.refresh();
    await settle(1);
    http.expectOne(url).flush({ pack: pack({ id: 'm2' }), job: null });
    await refreshed;
    expect(maps.outdated()).toBe(true);

    await maps.remove();
    expect(maps.local()).toBeNull();
    expect(await store.get('p1')).toBeUndefined();
  });

  it('finishes a build by polling when live updates do not arrive', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    try {
      const { maps, http, polled } = setup();
      const opened = maps.open('p1');
      await settle(1);
      http.expectOne(url).flush({ pack: null, job: null });
      await opened;
      void maps.build();
      await settle(1);
      http.expectOne({ method: 'POST', url }).flush(job('queued'));
      await settle(1);
      vi.advanceTimersByTime(3000);
      polled.next(job('failed', { error: 'Calc service unreachable.' }));
      await settle(1);
      http.expectOne(url).flush({ pack: null, job: null });
      await settle(1);
      expect(maps.error()).toBe('Calc service unreachable.');
      expect(maps.building()).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it('picks up a build another person started', async () => {
    const { maps, http, updates } = setup();
    const opened = maps.open('p1');
    await settle(1);
    http.expectOne(url).flush({ pack: null, job: job('running') });
    await opened;
    expect(maps.building()?.status).toBe('running');
    updates.next(job('cancelled'));
    updates.complete();
    await settle(1);
    http.expectOne(url).flush({ pack: null, job: null });
    await settle(1);
    expect(maps.building()).toBeNull();
  });
});
