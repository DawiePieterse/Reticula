import { Component, input, output, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { JobsService } from '../../core/jobs/jobs.service';
import { LvMap } from './lv-map';
import { OptionsPage } from './options-page';
import { idle } from '../../../testing/idle';

@Component({ selector: 'app-lv-map', template: '<span class="map-stub">{{ option()?.construction }}</span>' })
class LvMapStub {
  readonly option = input<{ construction: string } | null>(null);
  readonly colourBy = input<string>();
  readonly selectedId = input<string | null>();
  readonly select = output<string>();
}

const site = (id: string, notes: string | null) => ({ type: 'Feature', id, geometry: { type: 'Point', coordinates: [28.1, -25.52] }, properties: { kind: 'transformer', notes, createdAt: '', version: 1 } });
const traced = (value: number) => ({ value, unit: '', formula_id: 'f', formula: 'C = capex + losses', clause: '', rules_hash: 'h', inputs: [] });
const design = (label: string, construction: string, moved: number) => ({ position_id: 1, position: [28.1, -25.52], position_label: label, moved_m: moved, construction, transformer_kva: null, min_feeder: 0, phase_offset: 0, upsized: [] });
const option = (objective: string, title: string, construction: string, capex: number, life: number, spare: number, close = false) => ({
  objective, title, design: design(objective === 'capex' ? 'marked site' : 'load centre', construction, objective === 'capex' ? 0 : 84),
  option: {
    construction, passed: true, failed_checks: 0, converged: true, overhead: null,
    network: { source_id: 'S', nodes: [], customers: [], branches: [{ id: 'B1', kind: 'feeder', conductor: construction === 'overhead' ? 'ABC-50' : 'CU-PVC-35' }, { id: 'B2', kind: 'service', conductor: 'SC-10CU' }] },
    analysis: { transformer_kva: 200, worst_vdrop_pct: traced(6.2), checks: [], branches: [], nodes: [], customers: [], feeder_ends: [], demand_kva: traced(150), max_fault_ka: traced(5), unverified: [] },
    cost: { total: capex, currency: 'ZAR', lines: [], rates: 'r', rate_date: '2026-10-01', missing_rates: [], note: '' },
  },
  lifetime: { total: traced(life), annual_losses_kwh: 9600 },
  spare: { transformer_pct: 20, thermal_pct: 40, voltage_pct: spare, spare_pct: spare },
  trail: ['Start: overhead, transformer at the marked site', 'Feeders no smaller than ABC-50'], notes: objective === 'capex' ? [] : ['The transformer is not at the marked site: confirm the new position on site.'],
  runner_up: null, too_close_to_call: close,
});
const result = {
  rules: 'eskom/0.5.0', rules_hash: 'h', issues: [{ severity: 'error', code: 'customer_unconnected', message: 'Some buildings are more than 35 m from any LV route.', count: 1, samples: ['5018'] }], evaluations: 64, feasible: 50, positions: 7, capex_ceiling: 600000, uncertainty_pct: 15, currency: 'ZAR', rate_date: '2026-10-01',
  baseline: { design: design('marked site', 'overhead', 0), passed: false, failed_checks: 3, transformer_kva: 200, capex: 480000, lifetime_cost: 700000, spare_pct: -5 },
  options: [option('capex', 'Lowest capital cost', 'overhead', 445742, 649915, 7.7, true), option('lifetime', 'Lowest lifetime cost', 'overhead', 452818, 640308, 12.3, true),
    option('spare', 'Most spare capacity', 'underground', 590000, 700000, 36.5)],
  close_calls: [{ a: 'capex', b: 'lifetime', measure: 'capex', difference_pct: 1.6, band_pct: 15 }],
  assumptions: ['Lifetime: 25 years, discount rate 8 %.'], unverified: ['optimisation'],
};
const run = { id: 'r1', kind: 'options', status: 'succeeded', jobId: 'j1', parameters: {}, rulesRef: 'eskom/0.5.0', rulesHash: 'h', passed: true, summary: null, error: null, createdAt: '2026-10-04T10:00:00Z', finishedAt: null };

describe('OptionsPage', () => {
  async function setup() {
    TestBed.configureTestingModule({
      imports: [OptionsPage],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: AuthService, useValue: { isEngineer: signal(true) } },
        { provide: JobsService, useValue: { watch: (id: string) => of({ id, kind: 'design.options', status: 'succeeded', progressPct: 100, message: null, error: null, startedAt: null, finishedAt: null }) } },
      ],
    }).overrideComponent(OptionsPage, { remove: { imports: [LvMap] }, add: { imports: [LvMapStub] } });
    const fixture = TestBed.createComponent(OptionsPage);
    fixture.componentRef.setInput('id', 'p1');
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await idle();
    http.expectOne('/api/projects/p1/candidates').flush({ type: 'FeatureCollection', features: [site('t1', 'North'), site('t2', null)] });
    http.expectOne('/api/projects/p1/option-searches').flush([]);
    await idle();
    await fixture.whenStable();
    return { fixture, http, el: fixture.nativeElement as HTMLElement };
  }

  const type = (el: HTMLElement, name: string, value: string) => {
    const i = el.querySelector<HTMLInputElement>(`input[name="${name}"]`)!;
    i.value = value;
    i.dispatchEvent(new Event('input'));
  };

  it('sends the run parameters and compares the three options side by side', async () => {
    const { fixture, http, el } = await setup();
    el.querySelector<HTMLInputElement>('input[name="c-underground"]')!.click();
    el.querySelector<HTMLInputElement>('input[name="o-spare"]')!.click();
    type(el, 'discountRatePct', '10');
    type(el, 'moveRadiusM', '200');
    await fixture.whenStable();
    [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Search options'))!.click();
    await idle();
    const req = http.expectOne({ method: 'POST', url: '/api/projects/p1/option-searches' });
    expect(req.request.body).toEqual({
      transformerCandidateId: 't1', constructions: ['overhead'], objectives: ['capex', 'lifetime'], capexCeiling: null,
      lifetime: { periodYears: null, discountRatePct: 10, energyCostPerKwh: null, loadGrowthPct: null }, allowMove: true, moveRadiusM: 200, maxEvaluations: null,
    });
    req.flush({ run: { ...run, status: 'queued' }, job: { id: 'j1', kind: 'design.options', status: 'queued', progressPct: 0, message: null, error: null, startedAt: null, finishedAt: null } });
    await idle();
    http.expectOne('/api/projects/p1/option-searches').flush([run]);
    await idle();
    http.expectOne('/api/projects/p1/option-searches/r1').flush({ run, result });
    await idle();
    await fixture.whenStable();

    expect(el.querySelector('.banner.warn')?.textContent).toContain('optimisation');
    expect(el.textContent).toContain('64 designs checked in full, 50 pass every check');
    expect(el.textContent).toContain('fails 3 check(s)');
    expect(el.querySelector('ul.issues li.error')?.textContent).toContain('(e.g. 5018)');
    const heads = [...el.querySelectorAll('table.compare thead th')].map((h) => h.textContent!.trim());
    expect(heads[1]).toContain('Lowest capital cost');
    expect(heads[1]).toContain('too close to call');
    expect(heads[3]).not.toContain('too close to call');
    const row = (label: string) => [...el.querySelectorAll('table.compare tbody tr')].find((r) => r.querySelector('th')?.textContent === label)!;
    expect(row('Capital cost').querySelector('td.best')?.textContent).toContain('445,742');
    expect(row('Lifetime cost').querySelector('td.best')?.textContent).toContain('640,308');
    expect(row('Spare capacity').querySelector('td.best')?.textContent).toContain('36.5');
    expect(row('Transformer').textContent).toContain('load centre (84 m)');
    expect(row('Feeders').textContent).toContain('CU-PVC-35');
    expect(row('Feeders').textContent).not.toContain('SC-10CU');
    expect(el.querySelector('ul.close')?.textContent).toContain('capex differ by 1.6 %');
    expect(el.querySelector('.map-stub')?.textContent).toBe('overhead');

    (el.querySelectorAll('table.compare thead th')[3] as HTMLElement).click();
    await fixture.whenStable();
    expect(el.querySelector('.map-stub')?.textContent).toBe('underground');
  });
});
