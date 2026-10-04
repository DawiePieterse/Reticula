import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { JobsService } from '../../core/jobs/jobs.service';
import { BulkSupplyPage } from './bulk-supply-page';
import { idle } from '../../../testing/idle';

const site = (id: string, notes: string | null) => ({ type: 'Feature', id, geometry: { type: 'Point', coordinates: [28.1, -25.52] }, properties: { kind: 'transformer', notes, createdAt: '', version: 1 } });
const mvRun = { id: 'm1', kind: 'mv', status: 'succeeded', jobId: 'j0', parameters: {}, rulesRef: 'eskom/0.4.0', rulesHash: 'h', passed: true, summary: null, error: null, createdAt: '2026-10-04T09:00:00Z', finishedAt: null };
const run = { ...mvRun, id: 'b1', kind: 'bulk', passed: false, createdAt: '2026-10-04T10:00:00Z' };
const cp = (over: object = {}) => ({
  lon: 28.09, lat: -25.514, voltageKv: 11, availableCapacityKva: null, faultMvaMax: null, faultMvaMin: null, xr: null, sendingVoltagePct: null, reference: null,
  missing: ['available capacity', 'fault level'], updatedAt: '', ...over,
});
const result = {
  rules: 'eskom/0.4.0', rules_hash: 'h',
  buses: [
    { id: 'SUPPLY', kind: 'supply', vn_kv: 11, v_pct: 100, ikss3_max_ka: 7.87, ikss1_min_ka: 0.1 },
    { id: 'LV:t1', kind: 'lv', vn_kv: 0.4, v_pct: 102.07, ikss3_max_ka: 6.1, ikss1_min_ka: 5.2 },
  ],
  lines: [{ id: 'MB1', conductor: 'MV-FOX', loading_pct: 9.7, current_a: 19.5, losses_kw: 0.4 }],
  transformers: [{ site_id: 't1', loading_pct: 92.7, tap_pos: 1, lv_v_pct: 102.07, losses_kw: 2.1 }],
  supply_kva: 370.85, supply_kw: 333.8, losses_kw: 4.2, available_capacity_kva: 300, notified_max_demand_kva: 400,
  bulk_feeder: { id: 'MB1', conductor: 'MV-FOX', loading_pct: 9.7, current_a: 19.5, losses_kw: 0.4 },
  checks: [
    { code: 'sc_max', subject: 'SUPPLY', passed: true, value: 7.87, limit: 20, unit: 'kA', message: '', clause: 'B-01' },
    { code: 'supply_capacity', subject: 'connection point', passed: false, value: 370.9, limit: 300, unit: 'kVA', message: '', clause: 'B-01' },
  ],
  passed: false, assumptions: ['Connection point R/X taken as 0.1 (not given by the authority).'], unverified: ['bulk'],
};

describe('BulkSupplyPage', () => {
  async function setup(point: object | null) {
    TestBed.configureTestingModule({
      imports: [BulkSupplyPage],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: AuthService, useValue: { isEngineer: signal(true) } },
        { provide: JobsService, useValue: { watch: (id: string) => of({ id, kind: 'design.bulk', status: 'succeeded', progressPct: 100, message: null, error: null, startedAt: null, finishedAt: null }) } },
      ],
    });
    const fixture = TestBed.createComponent(BulkSupplyPage);
    fixture.componentRef.setInput('id', 'p1');
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await idle();
    http.expectOne('/api/projects/p1/connection-point').flush(point);
    http.expectOne('/api/projects/p1/candidates').flush({ type: 'FeatureCollection', features: [site('t1', 'North')] });
    http.expectOne('/api/projects/p1/mv-designs').flush([mvRun]);
    http.expectOne('/api/projects/p1/bulk-studies').flush([]);
    await idle();
    http.expectOne('/api/projects/p1/mv-designs/m1').flush({ run: mvRun, result: { mv_network: { supply_id: 'SUPPLY', nodes: [{ id: 'SUPPLY', kind: 'supply', lon: 28.09, lat: -25.514, site_id: null }], branches: [] } } });
    await idle();
    await fixture.whenStable();
    return { fixture, http, el: fixture.nativeElement as HTMLElement };
  }

  const button = (el: HTMLElement, text: string) => [...el.querySelectorAll('button')].find((b) => b.textContent?.includes(text))!;
  const type = (el: HTMLElement, name: string, value: string) => {
    const input = el.querySelector<HTMLInputElement>(`input[name="${name}"]`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  };

  it('stops the study until the capacity and fault level are entered, then saves the connection point', async () => {
    const { fixture, http, el } = await setup(null);
    expect(el.querySelector('.banner.warn')?.textContent).toContain('connection point');
    expect(button(el, 'Run bulk supply study').disabled).toBe(true);
    expect(button(el, 'Save connection point').disabled).toBe(true);

    button(el, "Use the MV design's supply point").click();
    await fixture.whenStable();
    type(el, 'reference', 'ESKOM/Q/123');
    await fixture.whenStable();
    button(el, 'Save connection point').click();
    await idle();
    const put = http.expectOne({ method: 'PUT', url: '/api/projects/p1/connection-point' });
    expect(put.request.body).toEqual({ lon: 28.09, lat: -25.514, voltageKv: 11, availableCapacityKva: null, faultMvaMax: null, faultMvaMin: null, xr: null, sendingVoltagePct: null, reference: 'ESKOM/Q/123' });
    put.flush(cp({ reference: 'ESKOM/Q/123' }));
    await idle();
    await fixture.whenStable();
    expect(el.querySelector('.banner.warn')?.textContent).toContain('available capacity and fault level');
    expect(button(el, 'Run bulk supply study').disabled).toBe(true);
  });

  it('runs the study and shows supply, NMD, faults, checks and assumptions', async () => {
    const { fixture, http, el } = await setup(cp({ availableCapacityKva: 300, faultMvaMax: 150, missing: [] }));
    expect(el.querySelector<HTMLInputElement>('input[name="faultMvaMax"]')!.value).toBe('150');
    expect(el.querySelector('.banner.warn')).toBeNull();
    button(el, 'Run bulk supply study').click();
    await idle();
    const post = http.expectOne({ method: 'POST', url: '/api/projects/p1/bulk-studies' });
    expect(post.request.body).toEqual({ mvDesignRunId: null });
    post.flush({ run: { ...run, status: 'queued' }, job: { id: 'j1', kind: 'design.bulk', status: 'queued', progressPct: 0, message: null, error: null, startedAt: null, finishedAt: null } });
    await idle();
    http.expectOne('/api/projects/p1/bulk-studies').flush([run]);
    await idle();
    http.expectOne('/api/projects/p1/bulk-studies/b1').flush({ run, result });
    await idle();
    await fixture.whenStable();

    expect(el.querySelector('.banner.warn')?.textContent).toContain('bulk');
    expect(el.querySelector('.summary')?.textContent).toContain('1 checks fail');
    const supply = el.querySelector('dl.supply')!.textContent!;
    expect(supply).toContain('370.9 kVA');
    expect(supply).toContain('400 kVA');
    expect(supply).toContain('MV-FOX');
    const buses = [...el.querySelectorAll('table.buses tbody tr')].map((r) => r.textContent);
    expect(buses[0]).toContain('Connection point');
    expect(buses[1]).toContain('North LV');
    expect(buses[1]).toContain('6.10 kA');
    expect(el.querySelector('table.trafos')?.textContent).toContain('North');
    const first = el.querySelector('table.checks tbody tr')!;
    expect(first.textContent).toContain('Capacity at the connection point');
    expect(first.classList).toContain('bad');
    expect(el.querySelector('.assumptions')?.textContent).toContain('R/X');
  });
});
