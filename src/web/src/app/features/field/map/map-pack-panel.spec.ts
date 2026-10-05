import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { ConnectivityService } from '../../../core/connectivity.service';
import { MapPackPanel } from './map-pack-panel';
import { MapPacks } from './map-packs.service';

function setup(state: Partial<Record<'local' | 'server' | 'building' | 'downloading' | 'error' | 'outdated', unknown>>, online = true) {
  const calls: string[] = [];
  const maps = {
    local: signal(state.local ?? null), server: signal(state.server ?? null), building: signal(state.building ?? null),
    downloading: signal(state.downloading ?? null), error: signal(state.error ?? null), outdated: signal(state.outdated ?? false),
    build: () => calls.push('build'), download: () => calls.push('download'), remove: () => calls.push('remove'),
  };
  TestBed.configureTestingModule({
    imports: [MapPackPanel],
    providers: [{ provide: MapPacks, useValue: maps }, { provide: ConnectivityService, useValue: { online: signal(online) } }],
  });
  const fixture = TestBed.createComponent(MapPackPanel);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const click = (text: string) => [...el.querySelectorAll('button')].find((b) => b.textContent?.includes(text))!.click();
  return { el, calls, click };
}

const pack = { id: 'm2', sizeBytes: 2_400_000, builtAt: '2026-10-05T07:00:00Z' };

describe('MapPackPanel', () => {
  it('offers to prepare the map when the server has none', () => {
    const { el, calls, click } = setup({ server: { pack: null, job: null } });
    expect(el.textContent).toContain('Prepare the map before going out');
    click('Prepare offline map');
    expect(calls).toEqual(['build']);
  });

  it('offers to save a prepared map', () => {
    const { el, calls, click } = setup({ server: { pack, job: null } });
    expect(el.textContent).toContain('Ready to save: 2.4 MB');
    click('Save on this tablet');
    expect(calls).toEqual(['download']);
  });

  it('shows the saved map and offers a newer one', () => {
    const { el, calls, click } = setup({ local: { savedAt: '2026-10-04T07:00:00Z', sizeBytes: 2_000_000 }, server: { pack, job: null }, outdated: true });
    expect(el.textContent).toContain('Saved on this tablet 4 Oct · 2.0 MB');
    click('Update (2.4 MB)');
    click('Remove from this tablet');
    expect(calls).toEqual(['download', 'remove']);
  });

  it('shows small maps in kB', () => {
    expect(setup({ local: { savedAt: '2026-10-04T07:00:00Z', sizeBytes: 4780 } }).el.textContent).toContain('· 5 kB');
  });

  it('shows build and download progress', () => {
    expect(setup({ building: { message: 'Cutting the map for the project area', progressPct: 10 } }).el.textContent).toContain('Preparing the map… Cutting the map');
  });

  it('says the background needs a connection when offline without a map', () => {
    const { el } = setup({}, false);
    expect(el.textContent).toContain('the background map needs a connection');
    expect(el.querySelector('button')).toBeNull();
  });
});
