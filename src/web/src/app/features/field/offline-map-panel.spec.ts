import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { of } from 'rxjs';
import { ConnectivityService } from '../../core/connectivity.service';
import { JobsService } from '../../core/jobs/jobs.service';
import { OfflineMapPanel } from './offline-map-panel';
import { TilePacks } from './tile-packs';
import { idle } from '../../../testing/idle';

const pack = { minZoom: 12, maxZoom: 18, tileCount: 900, sizeBytes: 2_097_152, sha256: 'abc123', source: 'http://t/{z}/{x}/{y}.png', attribution: '© OSM', bounds: [28, -26, 28.1, -25.9], builtAt: '2026-10-04T10:00:00Z' };
const status = (over = {}) => ({ sourceConfigured: true, pack, estimate: { minZoom: 12, maxZoom: 18, tiles: 950 }, estimateProblem: null, ...over });

function setup(online = true) {
  const watch = vi.fn((id: string) => of({ id, kind: 'maps.tile-pack', status: 'succeeded', progressPct: 100, message: null, error: null, startedAt: null, finishedAt: null }));
  TestBed.configureTestingModule({
    imports: [OfflineMapPanel],
    providers: [
      provideHttpClient(), provideHttpClientTesting(),
      { provide: ConnectivityService, useValue: { online: signal(online) } },
      { provide: JobsService, useValue: { watch } },
    ],
  });
  const fixture = TestBed.createComponent(OfflineMapPanel);
  fixture.componentRef.setInput('projectId', 'p1');
  fixture.detectChanges();
  return { fixture, http: TestBed.inject(HttpTestingController), el: fixture.nativeElement as HTMLElement, packs: TestBed.inject(TilePacks), watch };
}

const settle = async (f: { whenStable(): Promise<unknown> }) => {
  await idle();
  await f.whenStable();
};
const button = (el: HTMLElement, text: string) => [...el.querySelectorAll('button')].find((b) => b.textContent?.includes(text));

describe('OfflineMapPanel', () => {
  it('downloads the server\'s map to the tablet and keeps it across restarts', async () => {
    const { fixture, http, el } = setup();
    await idle();
    http.expectOne('/api/projects/p1/tile-pack').flush(status());
    await settle(fixture);
    expect(el.textContent).toContain('No offline map on this tablet');
    button(el, 'Download to this tablet (2 MB)')!.click();
    await idle();
    http.expectOne('/api/projects/p1/tile-pack/file').flush(new Uint8Array([80, 77, 84]).buffer);
    await settle(fixture);
    expect(el.textContent).toContain('On this tablet: zoom 12–18, 2 MB');
    expect(button(el, 'Download')).toBeUndefined();

    TestBed.resetTestingModule();
    const again = setup(false);
    await settle(again.fixture);
    expect(again.el.textContent).toContain('On this tablet: zoom 12–18');
    const local = again.packs.local().get('p1')!;
    expect(new Uint8Array(local.bytes)).toEqual(new Uint8Array([80, 77, 84]));
  });

  it('builds a map on the server, then downloads it', async () => {
    const { fixture, http, el, watch } = setup();
    await idle();
    http.expectOne('/api/projects/p1/tile-pack').flush(status({ pack: null }));
    await settle(fixture);
    expect(el.textContent).toContain('950 tiles, zoom 12–18');
    button(el, 'Build offline map')!.click();
    await idle();
    http.expectOne({ method: 'POST', url: '/api/projects/p1/tile-pack' }).flush({ id: 'j1', kind: 'maps.tile-pack', status: 'queued', progressPct: 0, message: null, error: null, startedAt: null, finishedAt: null });
    await idle();
    expect(watch).toHaveBeenCalledWith('j1');
    http.expectOne('/api/projects/p1/tile-pack').flush(status());
    await idle();
    http.expectOne('/api/projects/p1/tile-pack/file').flush(new ArrayBuffer(4));
    await settle(fixture);
    expect(el.textContent).toContain('On this tablet');
  });

  it('offers a newer server map and lets the tablet drop its copy', async () => {
    const first = setup();
    await idle();
    first.http.expectOne('/api/projects/p1/tile-pack').flush(status());
    await settle(first.fixture);
    button(first.el, 'Download to this tablet')!.click();
    await idle();
    first.http.expectOne('/api/projects/p1/tile-pack/file').flush(new ArrayBuffer(4));
    await settle(first.fixture);
    TestBed.resetTestingModule();

    const { fixture, http, el } = setup();
    await idle();
    http.expectOne('/api/projects/p1/tile-pack').flush(status({ pack: { ...pack, sha256: 'def456' } }));
    await settle(fixture);
    expect(button(el, 'Download the newer map')).toBeTruthy();
    button(el, 'Remove from this tablet')!.click();
    await settle(fixture);
    expect(el.textContent).toContain('No offline map on this tablet');
  });

  it('says when the server cannot build maps', async () => {
    const { fixture, http, el } = setup();
    await idle();
    http.expectOne('/api/projects/p1/tile-pack').flush(status({ sourceConfigured: false, pack: null }));
    await settle(fixture);
    expect(el.textContent).toContain('no offline tile source configured');
    expect(button(el, 'Build')).toBeUndefined();
  });
});
