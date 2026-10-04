import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AdmdForm, LoadPoint } from './field.api';
import { LoadTool } from './load-tool';
import { idle } from '../../../testing/idle';

const FORM: AdmdForm = {
  rules_hash: 'abc',
  indicators: [{ key: 'dwelling', label: 'Dwelling type', type: 'choice', options: ['informal', 'brick_small'] }],
  multi_indicators: [{ key: 'appliances', label: 'Visible appliances', type: 'multi', options: ['fridge', 'geyser'] }],
  band_indicators: [{ key: 'stand_size_m2', label: 'Stand size', type: 'number', unit: 'm²' }],
  special_loads: { school: 25, shop: 5, other: 2 },
  load_classes: [
    { code: 'informal_settlement', description: 'Informal settlement', table: 'nrs034_15y', admd_kva: 1.3, income_min_zar: 800, income_max_zar: 1500, usable: true },
    { code: 'township_area', description: 'Township area', table: 'nrs034_15y', admd_kva: 2.37, income_min_zar: 1500, income_max_zar: 3000, usable: true },
    { code: 'c8', description: 'Urban town house II', table: 'sans507_15y', admd_kva: 5.64, income_min_zar: null, income_max_zar: null, usable: false },
  ],
};

const lp = (over: Partial<LoadPoint> = {}): LoadPoint => ({
  id: 'lp1', buildingId: 'b1', kind: 'residential', specialLoad: null, observations: {}, classOverride: null, incomeBand: 'low', category: 'township_area',
  estimatedKva: 1.5, kva: 1.5, overridden: false, overrideReason: null, missing: ['roof'], status: 'estimated', updatedAt: '', version: 3, ...over,
});

async function setup(type = 'house', existing: LoadPoint | null = null) {
  TestBed.configureTestingModule({ imports: [LoadTool], providers: [provideHttpClient(), provideHttpClientTesting()] });
  const fixture = TestBed.createComponent(LoadTool);
  fixture.componentRef.setInput('projectId', 'p1');
  fixture.componentRef.setInput('buildingId', 'b1');
  fixture.componentRef.setInput('buildingType', type);
  fixture.componentRef.setInput('form', FORM);
  fixture.componentRef.setInput('existing', existing);
  await fixture.whenStable();
  return { fixture, http: TestBed.inject(HttpTestingController), el: fixture.nativeElement as HTMLElement };
}

const settle = async (f: { whenStable(): Promise<unknown> }) => {
  await new Promise((r) => setTimeout(r));
  await f.whenStable();
};
const button = (el: HTMLElement, text: string) => [...el.querySelectorAll('button')].find((b) => b.textContent?.includes(text))!;

describe('LoadTool', () => {
  it('renders the observation form from the rules and saves the observations', async () => {
    const { fixture, http, el } = await setup();
    expect(el.textContent).toContain('Dwelling type');
    expect([...el.querySelectorAll('select option')].map((o) => o.textContent)).toContain('brick small');

    const select = el.querySelector<HTMLSelectElement>('select[name="dwelling"]')!;
    select.value = 'brick_small';
    select.dispatchEvent(new Event('change'));
    el.querySelector<HTMLInputElement>('.chip input')!.click();
    const size = el.querySelector<HTMLInputElement>('input[name="stand_size_m2"]')!;
    size.value = '450';
    size.dispatchEvent(new Event('input'));
    await settle(fixture);

    button(el, 'Save load').click();
    await idle();
    const req = http.expectOne({ method: 'PUT', url: '/api/projects/p1/buildings/b1/load' });
    expect(req.request.body).toEqual({
      kind: 'residential', observations: { dwelling: 'brick_small', appliances: ['fridge'], stand_size_m2: 450 },
      specialLoad: null, overrideKva: null, overrideReason: null, version: null, loadClass: null,
    });
    req.flush(lp());
    await settle(fixture);
    expect(el.textContent).toContain('1.5 kVA');
    expect(el.textContent).toContain('Township area');
    expect(el.textContent).toContain('Not recorded, scored as zero: roof');
  });

  it('lets the engineer choose a class and blocks unverified ones', async () => {
    const { fixture, http, el } = await setup();
    const options = [...el.querySelectorAll<HTMLOptionElement>('select[name="loadClass"] option')];
    const township = options.find((o) => o.value === 'township_area')!.textContent!;
    expect(township).toContain('Township area · 2.37 kVA');
    expect(township).toContain('/month');
    expect(options[0].textContent).toContain('From the observations');
    expect(options.find((o) => o.value === 'c8')?.disabled).toBe(true);

    const select = el.querySelector<HTMLSelectElement>('select[name="loadClass"]')!;
    select.value = 'informal_settlement';
    select.dispatchEvent(new Event('change'));
    await settle(fixture);
    button(el, 'Save load').click();
    await idle();
    const req = http.expectOne('/api/projects/p1/buildings/b1/load');
    expect(req.request.body.loadClass).toBe('informal_settlement');
    req.flush(lp({ category: 'informal_settlement', classOverride: 'informal_settlement', kva: 1.3, estimatedKva: 1.3 }));
    await settle(fixture);
    expect(el.textContent).toContain('Informal settlement');
    expect(el.textContent).toContain('(chosen)');
  });

  it('defaults a school to a special load', async () => {
    const { fixture, http, el } = await setup('school');
    button(el, 'Save load').click();
    await idle();
    expect(http.expectOne('/api/projects/p1/buildings/b1/load').request.body).toMatchObject({ kind: 'special', specialLoad: 'school' });
    await settle(fixture);
  });

  it('needs a reason before an override can be saved', async () => {
    const { fixture, el } = await setup();
    const toggle = el.querySelector<HTMLInputElement>('input[name="ovr"]')!;
    toggle.click();
    await settle(fixture);
    const kva = el.querySelector<HTMLInputElement>('input[name="kva"]')!;
    kva.value = '2.2';
    kva.dispatchEvent(new Event('input'));
    await settle(fixture);
    expect(button(el, 'Save load').disabled).toBe(true);
    const reason = el.querySelector<HTMLInputElement>('input[name="reason"]')!;
    reason.value = 'Spaza shop at the back';
    reason.dispatchEvent(new Event('input'));
    await settle(fixture);
    expect(button(el, 'Save load').disabled).toBe(false);
  });

  it('shows a conflict and the other version', async () => {
    const { fixture, http, el } = await setup('house', lp());
    button(el, 'Save load').click();
    await idle();
    http.expectOne('/api/projects/p1/buildings/b1/load').flush(lp({ kva: 3, version: 4 }), { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Someone else changed this load');
    expect(el.textContent).toContain('3 kVA');
  });
});
