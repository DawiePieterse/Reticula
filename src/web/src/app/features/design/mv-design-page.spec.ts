import { Component, input, output, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { JobsService } from '../../core/jobs/jobs.service';
import { MvDesignPage } from './mv-design-page';
import { MvMap } from './mv-map';
import { idle } from '../../../testing/idle';

@Component({ selector: 'app-mv-map', template: '<span class="map-stub">{{ result()?.sites?.length }}</span>' })
class MvMapStub {
  readonly result = input<{ sites: unknown[] } | null>(null);
  readonly selectedSite = input<string | null>();
  readonly select = output<string>();
}

const site = (id: string, notes: string | null) => ({ type: 'Feature', id, geometry: { type: 'Point', coordinates: [28.1, -25.52] }, properties: { kind: 'transformer', notes, createdAt: '', version: 1 } });
const siteResult = (id: string, tap: number | null) => ({
  placement: { site_id: id, customers: ['a', 'b'], demand_kva: 140, design_kva: 140, unit: 'pole_mount', rating_kva: 200, z_pct: 4.5, x_r: 3, utilisation_pct: 70, note: null },
  lon: 28.1, lat: -25.52, lv: null, lv_passed: true, lv_worst_vdrop_pct: 7.8, mv_vdrop_pct: 0.6, regulation_pct: 1.9, tap_pct: tap, v_min_pct: 94.6, v_max_pct: 105,
});
const result = {
  rules: 'eskom/0.4.0', rules_hash: 'h', issues: [{ severity: 'warning', code: 'supply_assumed', message: 'Supply assumed at the end of the MV route.', count: 1, samples: [] }],
  sites: [siteResult('t1', 5), siteResult('t2', null)],
  mv_network: null,
  mv_analysis: { branches: [{ id: 'MB2', conductor: 'MV-FOX', length_m: 1231, demand_kva: 338, current_a: 17.7, rating_a: 160, loading_pct: 11, vdrop_pct_end: 0.49, sites: 2 }], site_vdrop_pct: {}, checks: [] },
  checks: [
    { code: 'supply_voltage_band', subject: 't2', passed: false, value: 88.1, limit: 90, unit: '%', message: 'no tap fits', clause: 'NRS048-2; idx MV-01' },
    { code: 'mv_thermal', subject: 'MB2', passed: true, value: 17.7, limit: 160, unit: 'A', message: 'ok', clause: 'SANS182; idx C-04' },
  ],
  passed: false, cost_lines: [{ item: 'MV MV-FOX', quantity: 1231, unit: 'm', rate: 250, amount: 307750 }], cost_total: 307750, currency: 'ZAR', rate_date: '2026-10-01', unverified: ['mv_design'],
};
const run = { id: 'r1', kind: 'mv', status: 'succeeded', jobId: 'j1', parameters: {}, rulesRef: 'eskom/0.4.0', rulesHash: 'h', passed: false, summary: null, error: null, createdAt: '2026-10-04T10:00:00Z', finishedAt: null };

describe('MvDesignPage', () => {
  async function setup() {
    TestBed.configureTestingModule({
      imports: [MvDesignPage],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: AuthService, useValue: { isEngineer: signal(true) } },
        { provide: JobsService, useValue: { watch: (id: string) => of({ id, kind: 'design.mv', status: 'succeeded', progressPct: 100, message: null, error: null, startedAt: null, finishedAt: null }) } },
      ],
    }).overrideComponent(MvDesignPage, { remove: { imports: [MvMap] }, add: { imports: [MvMapStub] } });
    const fixture = TestBed.createComponent(MvDesignPage);
    fixture.componentRef.setInput('id', 'p1');
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await idle();
    http.expectOne('/api/projects/p1/candidates').flush({ type: 'FeatureCollection', features: [site('t1', 'North'), site('t2', null)] });
    http.expectOne('/api/projects/p1/mv-designs').flush([]);
    await idle();
    await fixture.whenStable();
    return { fixture, http, el: fixture.nativeElement as HTMLElement };
  }

  it('designs the chosen sites and shows transformers, taps, checks and cost', async () => {
    const { fixture, http, el } = await setup();
    const boxes = el.querySelectorAll<HTMLInputElement>('fieldset.chips input[type="checkbox"]');
    expect(boxes.length).toBe(2);
    boxes[1].click();
    await fixture.whenStable();
    [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Design'))!.click();
    await idle();
    const req = http.expectOne({ method: 'POST', url: '/api/projects/p1/mv-designs' });
    expect(req.request.body).toEqual({ siteIds: ['t1'], lvConstruction: 'overhead', mvConstruction: 'overhead', supply: null });
    req.flush({ run: { ...run, status: 'queued' }, job: { id: 'j1', kind: 'design.mv', status: 'queued', progressPct: 0, message: null, error: null, startedAt: null, finishedAt: null } });
    await idle();
    http.expectOne('/api/projects/p1/mv-designs').flush([run]);
    await idle();
    http.expectOne('/api/projects/p1/mv-designs/r1').flush({ run, result });
    await idle();
    await fixture.whenStable();

    expect(el.querySelector('.banner.warn')?.textContent).toContain('mv_design');
    expect(el.textContent).toContain('Supply assumed at the end of the MV route.');
    expect(el.querySelector('.summary')?.textContent).toContain('1 checks fail');
    const rows = [...el.querySelectorAll('table.sites tbody tr')];
    expect(rows[0].textContent).toContain('North');
    expect(rows[0].textContent).toContain('+5 %');
    expect(rows[1].textContent).toContain('Transformer 2');
    expect(rows[1].textContent).toContain('none fits');
    const first = el.querySelector('table.checks tbody tr')!;
    expect(first.textContent).toContain('Customer voltage band');
    expect(first.textContent).toContain('idx MV-01');
    expect(el.querySelector('table.cost')?.textContent).toContain('307,750');
  });
});
