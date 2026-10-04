import { Component, input, output, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { JobsService } from '../../core/jobs/jobs.service';
import { LvDesignPage } from './lv-design-page';
import { LvMap } from './lv-map';
import { idle } from '../../../testing/idle';

@Component({ selector: 'app-lv-map', template: '<span class="map-stub">{{ option()?.construction }}</span>' })
class LvMapStub {
  readonly option = input<{ construction: string } | null>(null);
  readonly colourBy = input<string>();
  readonly selectedId = input<string | null>();
  readonly select = output<string>();
}

const site = { type: 'Feature', id: 'tx1', geometry: { type: 'Point', coordinates: [28.1, -25.52] }, properties: { kind: 'transformer', notes: 'corner', createdAt: '', version: 1 } };
const traced = (value: number, unit: string) => ({ value, unit, formula_id: 'f', formula: 'f', clause: 'c', rules_hash: 'h', inputs: [] });
const option = (construction: string, passed: boolean) => ({
  construction, converged: passed, passed, failed_checks: passed ? 0 : 1,
  network: { source_id: 'S', nodes: [{ id: 'S', kind: 'source', lon: 28.1, lat: -25.52, pole: null, stay: false, stays: 0 }, { id: 'N1', kind: 'pole', lon: 28.101, lat: -25.52, pole: 'WP-9-160', stay: true, stays: 1 }],
    branches: [{ id: 'B1', from_id: 'S', to_id: 'N1', kind: 'feeder', construction, conductor: 'ABC-70', length_m: 40, geometry: [[28.1, -25.52], [28.101, -25.52]], crosses_road: false }], customers: [] },
  analysis: {
    branches: [{ id: 'B1', conductor: 'ABC-70', length_m: 40, design_current_a: 120, current_by_phase: {}, rating_a: 180, derating: 1, loading_pct: 66.7, customers: 10 }],
    nodes: [{ id: 'S', vdrop_v: {}, vdrop_pct: 0, worst_phase: 'R' }, { id: 'N1', vdrop_v: {}, vdrop_pct: 3.1, worst_phase: 'R' }],
    customers: [], feeder_ends: [], demand_kva: traced(80, 'kVA'), transformer_kva: 100, max_fault_ka: traced(5.2, 'kA'), worst_vdrop_pct: traced(3.1, '%'),
    checks: [
      { code: 'thermal', subject: 'B1', passed: true, value: 120, limit: 180, unit: 'A', message: 'ok', clause: 'SANS1418-1; idx C-01' },
      ...(passed ? [] : [{ code: 'ground_clearance', subject: 'B1', passed: false, value: 4.8, limit: 5.5, unit: 'm', message: 'low', clause: 'SANS10280-1; idx OH-01' }]),
    ],
    unverified: ['lv_design'],
  },
  overhead: null, cost: { rates: 'indicative/2026-10', rate_date: '2026-10-01', currency: 'ZAR', lines: [{ item: 'Conductor ABC-70', quantity: 40, unit: 'm', rate: 240, amount: 9600 }], total: 9600, missing_rates: [], note: 'Indicative.' },
});
const comparison = (construction: string, passed: boolean) => ({ construction, passed, worst_vdrop_pct: 3.1, max_loading_pct: 66.7, transformer_kva: 100, min_end_fault_a: 900, route_length_m: 40, poles: 2, stays: 1, kiosks: 0, cost_total: passed ? 9600 : 20000, currency: 'ZAR' });
const result = { rules: 'eskom/0.3.0', rules_hash: 'abc', issues: [{ severity: 'warning', code: 'not_inspected', message: 'Some buildings were not inspected.', count: 2, samples: ['101'] }],
  options: [option('overhead', false), option('underground', true)], comparison: [comparison('overhead', false), comparison('underground', true)], unverified: ['lv_design', 'conductor ABC-70'] };
const run = (status = 'succeeded') => ({ id: 'run1', kind: 'lv', status, jobId: 'j1', parameters: { transformerCandidateId: 'tx1', constructions: ['overhead', 'underground'], transformerKva: null },
  rulesRef: 'eskom/0.3.0', rulesHash: 'abc', passed: false, summary: null, error: null, createdAt: '2026-10-04T10:00:00Z', finishedAt: null });

async function setup(runs: unknown[] = []) {
  TestBed.configureTestingModule({
    imports: [LvDesignPage],
    providers: [
      provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
      { provide: AuthService, useValue: { isEngineer: signal(true) } },
      { provide: JobsService, useValue: { watch: (id: string) => of({ id, kind: 'design.lv', status: 'succeeded', progressPct: 100, message: null, error: null, startedAt: null, finishedAt: null }) } },
    ],
  }).overrideComponent(LvDesignPage, { remove: { imports: [LvMap] }, add: { imports: [LvMapStub] } });
  const fixture = TestBed.createComponent(LvDesignPage);
  fixture.componentRef.setInput('id', 'p1');
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  await idle();
  http.expectOne('/api/projects/p1/candidates').flush({ type: 'FeatureCollection', features: [site] });
  http.expectOne('/api/projects/p1/lv-designs').flush(runs);
  await idle();
  return { fixture, http, el: fixture.nativeElement as HTMLElement };
}

const settle = async (f: { whenStable(): Promise<unknown> }) => {
  await idle();
  await f.whenStable();
};
const button = (el: HTMLElement, text: string) => [...el.querySelectorAll('button')].find((b) => b.textContent?.includes(text));

describe('LvDesignPage', () => {
  it('starts a design for the only site, then shows the comparison and failed checks with clauses', async () => {
    const { fixture, http, el } = await setup();
    await settle(fixture);
    const ug = el.querySelector<HTMLInputElement>('input[name="ug"]')!;
    ug.checked = true;
    ug.dispatchEvent(new Event('change'));
    await settle(fixture);
    button(el, 'Design')!.click();
    await idle();
    const req = http.expectOne({ method: 'POST', url: '/api/projects/p1/lv-designs' });
    expect(req.request.body).toEqual({ transformerCandidateId: 'tx1', constructions: ['overhead', 'underground'], transformerKva: null });
    req.flush({ run: run('queued'), job: { id: 'j1', kind: 'design.lv', status: 'queued', progressPct: 0, message: null, error: null, startedAt: null, finishedAt: null } });
    await idle();
    http.expectOne('/api/projects/p1/lv-designs').flush([run()]);
    await idle();
    http.expectOne('/api/projects/p1/lv-designs/run1').flush({ run: run(), result });
    await settle(fixture);

    expect(el.querySelector('.banner.warn')?.textContent).toContain('conductor ABC-70');
    expect(el.textContent).toContain('Some buildings were not inspected.');
    const compare = el.querySelector('table.compare')!.textContent!;
    expect(compare).toContain('Fail');
    expect(compare).toContain('ZAR 20,000');
    const failed = [...el.querySelectorAll('table.checks tbody tr')];
    expect(failed.length).toBe(1);
    expect(failed[0].textContent).toContain('Ground clearance');
    expect(failed[0].textContent).toContain('idx OH-01');
    expect(el.querySelector('.map-stub')?.textContent).toBe('overhead');

    button(el, 'underground')!.click();
    await settle(fixture);
    expect(el.querySelector('.map-stub')?.textContent).toBe('underground');
    expect(el.querySelectorAll('table.checks tbody tr').length).toBe(0);
  });

  it('opens the latest successful run on arrival and shows a failed run\'s reason', async () => {
    const failed = { ...run('failed'), id: 'run0', error: '3 buildings have no load recorded' };
    const { fixture, http, el } = await setup([failed, { ...run(), id: 'run1' }]);
    http.expectOne('/api/projects/p1/lv-designs/run1').flush({ run: run(), result });
    await settle(fixture);
    expect(el.querySelector('table.segments tbody tr')?.textContent).toContain('ABC-70');
    const select = el.querySelector<HTMLSelectElement>('select[name="runId"]')!;
    select.value = 'run0';
    select.dispatchEvent(new Event('change'));
    await idle();
    http.expectOne('/api/projects/p1/lv-designs/run0').flush({ run: failed, result: null });
    await settle(fixture);
    expect(el.textContent).toContain('This run failed: 3 buildings have no load recorded');
  });
});
