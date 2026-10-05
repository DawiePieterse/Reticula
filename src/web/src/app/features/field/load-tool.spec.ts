import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AdmdForm, LoadPoint, LoadRequest } from './field.api';
import { LoadTool } from './load-tool';
import { FieldSync } from './sync/field-sync.service';
import { FORM as BASE_FORM, loadPoint, settle, snapshot, syncTesting } from './sync/testing';

const FORM: AdmdForm = {
  ...BASE_FORM,
  load_classes: [
    { code: 'informal_settlement', description: 'Informal settlement', table: 'nrs034_15y', admd_kva: 1.3, income_min_zar: 800, income_max_zar: 1500, usable: true },
    { code: 'township_area', description: 'Township area', table: 'nrs034_15y', admd_kva: 2.37, income_min_zar: 1500, income_max_zar: 3000, usable: true },
    { code: 'c8', description: 'Urban town house II', table: 'sans507_15y', admd_kva: 5.64, income_min_zar: null, income_max_zar: null, usable: false },
  ],
};

const lp = (over: Partial<LoadPoint> = {}) => loadPoint('b1', { missing: ['roof'], ...over });

/** The tool for building b1 of a project opened offline; `existing` follows the device's view of its load. */
async function setup(type = 'house', existing: LoadPoint | null = null) {
  const t = syncTesting(false);
  await t.db.putSnapshot(snapshot({ loads: existing ? [existing] : [], form: FORM }));
  TestBed.configureTestingModule({ imports: [LoadTool], providers: t.providers });
  const sync = TestBed.inject(FieldSync);
  await sync.open('p1');
  const fixture = TestBed.createComponent(LoadTool);
  fixture.componentRef.setInput('buildingId', 'b1');
  fixture.componentRef.setInput('buildingType', type);
  fixture.componentRef.setInput('form', FORM);
  const follow = async () => {
    fixture.componentRef.setInput('existing', sync.view()!.loads.get('b1') ?? null);
    await settle();
    await fixture.whenStable();
  };
  await follow();
  const saved = async () => (await t.db.ops()).map((o) => (o.body as { req: LoadRequest }).req);
  return { ...t, sync, fixture, follow, saved, el: fixture.nativeElement as HTMLElement };
}

const button = (el: HTMLElement, text: string) => [...el.querySelectorAll('button')].find((b) => b.textContent?.includes(text))!;

describe('LoadTool', () => {
  it('renders the observation form from the rules and queues the observations; the kVA comes back from the server', async () => {
    const { el, follow, saved, online, sync } = await setup();
    expect(el.textContent).toContain('Dwelling type');
    expect([...el.querySelectorAll('select option')].map((o) => o.textContent)).toContain('brick small');

    const select = el.querySelector<HTMLSelectElement>('select[name="dwelling"]')!;
    select.value = 'brick_small';
    select.dispatchEvent(new Event('change'));
    el.querySelector<HTMLInputElement>('.chip input')!.click();
    const size = el.querySelector<HTMLInputElement>('input[name="stand_size_m2"]')!;
    size.value = '450';
    size.dispatchEvent(new Event('input'));
    await follow();

    button(el, 'Save load').click();
    await follow();
    expect((await saved())[0]).toMatchObject({
      kind: 'residential', observations: { dwelling: 'brick_small', appliances: ['fridge'], stand_size_m2: 450 },
      specialLoad: null, overrideKva: null, overrideReason: null, version: null, loadClass: null,
    });
    expect(el.textContent).toContain('The kVA is worked out when it syncs');
    expect(el.textContent).not.toContain('NaN');

    online.set(true);
    await settle();
    TestBed.inject(HttpTestingController)
      .expectOne({ method: 'PUT', url: '/api/projects/p1/buildings/b1/load' })
      .flush(lp({ observations: { dwelling: 'brick_small', appliances: ['fridge'], stand_size_m2: 450 } }));
    await settle();
    TestBed.inject(HttpTestingController).match('/api/projects/p1/field-progress').forEach((r) => r.flush({ assumptionsOpen: 1 }));
    await follow();
    expect(sync.queued()).toBe(0);
    expect(el.textContent).toContain('1.5 kVA');
    expect(el.textContent).toContain('Township area');
    expect(el.textContent).toContain('Not recorded, scored as zero: roof');
    // The form shows what was saved.
    expect(el.querySelector<HTMLSelectElement>('select[name="dwelling"]')!.value).toBe('brick_small');
  });

  it('lets the engineer choose a class and blocks unverified ones', async () => {
    const { el, follow, saved } = await setup();
    const options = [...el.querySelectorAll<HTMLOptionElement>('select[name="loadClass"] option')];
    const township = options.find((o) => o.value === 'township_area')!.textContent!;
    expect(township).toContain('Township area · 2.37 kVA');
    expect(township).toContain('/month');
    expect(options[0].textContent).toContain('From the observations');
    expect(options.find((o) => o.value === 'c8')?.disabled).toBe(true);

    const select = el.querySelector<HTMLSelectElement>('select[name="loadClass"]')!;
    select.value = 'informal_settlement';
    select.dispatchEvent(new Event('change'));
    await follow();
    button(el, 'Save load').click();
    await follow();
    expect((await saved())[0].loadClass).toBe('informal_settlement');
    expect(el.querySelector<HTMLSelectElement>('select[name="loadClass"]')!.value).toBe('informal_settlement');
  });

  it('defaults a school to a special load', async () => {
    const { el, follow, saved } = await setup('school');
    button(el, 'Save load').click();
    await follow();
    expect((await saved())[0]).toMatchObject({ kind: 'special', specialLoad: 'school' });
  });

  it('needs a reason before an override can be saved', async () => {
    const { fixture, el } = await setup();
    const toggle = el.querySelector<HTMLInputElement>('input[name="ovr"]')!;
    toggle.click();
    await fixture.whenStable();
    const kva = el.querySelector<HTMLInputElement>('input[name="kva"]')!;
    kva.value = '2.2';
    kva.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(button(el, 'Save load').disabled).toBe(true);
    const reason = el.querySelector<HTMLInputElement>('input[name="reason"]')!;
    reason.value = 'Spaza shop at the back';
    reason.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(button(el, 'Save load').disabled).toBe(false);
  });

  it('sends the load version seen and keeps the form while the person is editing', async () => {
    const { el, follow, saved, fixture } = await setup('house', lp({ observations: { dwelling: 'informal' } }));
    const select = el.querySelector<HTMLSelectElement>('select[name="dwelling"]')!;
    expect(select.value).toBe('informal');
    select.value = 'brick_small';
    select.dispatchEvent(new Event('change'));
    // A refresh brings a newer load while the person is part-way through: their edit stays.
    fixture.componentRef.setInput('existing', lp({ version: 9, observations: { dwelling: 'informal' } }));
    await fixture.whenStable();
    expect(el.querySelector<HTMLSelectElement>('select[name="dwelling"]')!.value).toBe('brick_small');

    fixture.componentRef.setInput('existing', lp());
    await fixture.whenStable();
    button(el, 'Save load').click();
    await follow();
    expect((await saved())[0]).toMatchObject({ version: 3, observations: { dwelling: 'brick_small' } });
  });
});
