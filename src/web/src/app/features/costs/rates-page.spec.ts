import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthService } from '../../core/auth/auth.service';
import { RatesPage } from './rates-page';
import { idle } from '../../../testing/idle';

const summary = { id: 'r1', name: 'Contractor 2026', basedOn: 'indicative/2026-10', rateDate: '2026-10-01', currency: 'ZAR', revision: 1, overrides: 0, updatedAt: '', version: 7 };
const detail = (rev = 1, abc = 165, override: object | null = null) => ({
  list: { ...summary, revision: rev, version: 7 + rev },
  rows: [
    { section: 'conductor_per_m', code: 'ABC-35', description: null, unit: 'm', rate: abc, assembly: null, assemblyRate: null, override },
    { section: 'pole_each', code: 'WP-9-160', description: 'LV pole 9 m', unit: 'each', rate: null, assembly: 'OH-POLE-9-160', assemblyRate: 5200, override: null },
    { section: 'materials', code: 'POLE-9-160', description: 'Wood pole 9 m', unit: 'each', rate: 3300, assembly: null, assemblyRate: null, override: null },
  ],
  overrides: [], assemblies: { 'OH-POLE-9-160': { description: 'LV pole 9 m', components: [{ material: 'POLE-9-160', qty: 1 }, { material: 'LAB-POLE-9', qty: 1 }] } },
});

describe('RatesPage', () => {
  async function setup() {
    TestBed.configureTestingModule({ imports: [RatesPage], providers: [provideHttpClient(), provideHttpClientTesting(), { provide: AuthService, useValue: { isEngineer: signal(true) } }] });
    const fixture = TestBed.createComponent(RatesPage);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await idle();
    http.expectOne('/api/rate-lists').flush({ shipped: ['indicative/2026-10'], lists: [summary] });
    await idle();
    await fixture.whenStable();
    return { fixture, http, el: fixture.nativeElement as HTMLElement };
  }

  it('creates a list from a shipped one', async () => {
    const { fixture, http, el } = await setup();
    const name = el.querySelector<HTMLInputElement>('input[name="newName"]')!;
    name.value = 'Tender rates';
    name.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    [...el.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Create')!.click();
    await idle();
    const req = http.expectOne({ method: 'POST', url: '/api/rate-lists' });
    expect(req.request.body).toEqual({ name: 'Tender rates', basedOn: 'indicative/2026-10', rateDate: null });
    req.flush(detail());
    await idle();
    http.expectOne('/api/rate-lists').flush({ shipped: ['indicative/2026-10'], lists: [summary] });
    await idle();
    await fixture.whenStable();
    expect(el.querySelector('section.detail h3')?.textContent).toContain('Contractor 2026');
  });

  it('overrides a rate with a date and source, and shows assemblies', async () => {
    const { fixture, http, el } = await setup();
    (el.querySelector('button.link') as HTMLButtonElement).click();
    await idle();
    http.expectOne('/api/rate-lists/r1').flush(detail());
    await idle();
    await fixture.whenStable();
    expect(el.textContent).toContain('assembly OH-POLE-9-160: 1 × POLE-9-160 + 1 × LAB-POLE-9');
    const rate = el.querySelector<HTMLInputElement>('input[name="rate-conductor_per_m/ABC-35"]')!;
    rate.value = '180';
    rate.dispatchEvent(new Event('input'));
    const date = el.querySelector<HTMLInputElement>('input[name="changeDate"]')!;
    date.value = '2026-09-15';
    date.dispatchEvent(new Event('input'));
    const source = el.querySelector<HTMLInputElement>('input[name="changeSource"]')!;
    source.value = 'Quote Q-17';
    source.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    const save = [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Save'))!;
    expect(save.textContent).toContain('Save 1 change(s)');
    save.click();
    await idle();
    const req = http.expectOne({ method: 'PUT', url: '/api/rate-lists/r1/rates' });
    expect(req.request.body).toEqual({ changes: [{ section: 'conductor_per_m', code: 'ABC-35', rate: 180, date: '2026-09-15', source: 'Quote Q-17' }], rateDate: null, version: 8 });
    req.flush(detail(2, 180, { section: 'conductor_per_m', code: 'ABC-35', rate: 180, previous: 165, date: '2026-09-15', source: 'Quote Q-17' }));
    await idle();
    http.expectOne('/api/rate-lists').flush({ shipped: [], lists: [summary] });
    await idle();
    await fixture.whenStable();
    expect(el.querySelector('table.rates tbody tr')?.textContent).toContain('2026-09-15 · Quote Q-17 · was 165.00');
  });
});
