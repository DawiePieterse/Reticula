import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AUTH_STORAGE_KEY } from '../../core/auth/auth.service';
import { LoadsPage } from './loads-page';

const schedule = {
  project: 'Soshanguve', rulesRef: 'eskom/0.1.0', rulesHash: 'abc123', generatedAt: '2026-10-04T06:00:00Z',
  rows: [
    { erf: '1', buildingId: 'b1', buildingType: 'house', buildingStatus: 'confirmed', loadKind: 'residential', category: 'R2', incomeBand: 'low', kva: 1.5, estimatedKva: 1.5, overridden: false, overrideReason: null, loadStatus: 'estimated' },
    { erf: '2', buildingId: 'b2', buildingType: 'house', buildingStatus: 'predicted', loadKind: null, category: null, incomeBand: null, kva: null, estimatedKva: null, overridden: false, overrideReason: null, loadStatus: null },
    { erf: '3', buildingId: 'b3', buildingType: 'shop', buildingStatus: 'confirmed', loadKind: 'special', category: 'shop', incomeBand: null, kva: 8, estimatedKva: 5, overridden: true, overrideReason: 'Bakery ovens', loadStatus: 'confirmed' },
  ],
  totals: { residentialCount: 1, specialCount: 1, diversityFactor: 2.5, residentialKva: 3.75, specialKva: 8, totalKva: 11.75, formula: 'S = ...', clause: 'NRS' },
};

async function setup(roles = ['engineer']) {
  localStorage.setItem(AUTH_STORAGE_KEY, JSON.stringify({ tokens: { accessToken: 'a', refreshToken: 'r', expiresAt: 0 }, user: { id: '1', email: 'x', displayName: 'X', registrationNo: null, roles } }));
  TestBed.configureTestingModule({ imports: [LoadsPage], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
  const fixture = TestBed.createComponent(LoadsPage);
  fixture.componentRef.setInput('id', 'p1');
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  const flush = () => {
    http.expectOne('/api/projects/p1/load-schedule').flush(schedule);
    http.expectOne('/api/projects/p1/assumptions?status=open').flush([
      { id: 'a1', subjectType: 'load_point', subjectId: 'lp1', code: 'admd_estimated', text: 'ADMD at erf 1 estimated as 1.5 kVA', status: 'open', createdAt: '', clearedAt: null, clearNote: null },
    ]);
    http.expectOne('/api/projects/p1/load-points').flush([{ id: 'lp1', buildingId: 'b1' }]);
  };
  flush();
  await new Promise((r) => setTimeout(r));
  await fixture.whenStable();
  return { fixture, http, flush, el: fixture.nativeElement as HTMLElement };
}

const settle = async (f: { whenStable(): Promise<unknown> }) => {
  await new Promise((r) => setTimeout(r));
  await f.whenStable();
};
const button = (el: HTMLElement, text: string) => [...el.querySelectorAll('button')].find((x) => x.textContent?.includes(text))!;

describe('LoadsPage', () => {
  afterEach(() => localStorage.clear());

  it('shows totals, missing loads and overrides', async () => {
    const { el } = await setup();
    expect(el.querySelector('.grand')?.textContent).toContain('11.75 kVA');
    expect(el.querySelector('tr.missing')?.textContent).toContain('No load recorded');
    expect(el.textContent).toContain('erf 3: Bakery ovens');
    expect(el.textContent).toContain('eskom/0.1.0 (abc123)');
  });

  it('lets the engineer confirm a load and clear an assumption', async () => {
    const { fixture, http, flush, el } = await setup();
    button(el, 'Confirm').click();
    http.expectOne({ method: 'POST', url: '/api/projects/p1/load-points/lp1/confirm' }).flush({});
    await settle(fixture);
    flush();
    await settle(fixture);

    const note = el.querySelector<HTMLInputElement>('.clear input')!;
    note.value = 'Checked on site with the owner';
    note.dispatchEvent(new Event('input'));
    button(el, 'Clear').click();
    const req = http.expectOne({ method: 'POST', url: '/api/projects/p1/assumptions/a1/clear' });
    expect(req.request.body).toEqual({ note: 'Checked on site with the owner' });
    req.flush(null);
    await settle(fixture);
    flush();
  });

  it('downloads the CSV through the authenticated client', async () => {
    const { fixture, http, el } = await setup();
    const create = vi.fn(() => 'blob:x');
    const revoke = vi.fn();
    Object.assign(URL, { createObjectURL: create, revokeObjectURL: revoke });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    button(el, 'Download CSV').click();
    http.expectOne('/api/projects/p1/load-schedule.csv').flush(new Blob(['erf,building_id']));
    await settle(fixture);
    expect(create).toHaveBeenCalled();
    expect(click).toHaveBeenCalled();
    click.mockRestore();
  });

  it('hides engineer actions from inspectors', async () => {
    const { el } = await setup(['inspector']);
    expect(button(el, 'Confirm')).toBeUndefined();
    expect(el.querySelector('.clear')).toBeNull();
  });
});
