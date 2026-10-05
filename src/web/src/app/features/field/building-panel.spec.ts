import { Component, input, output } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BuildingPanel } from './building-panel';
import { LoadTool } from './load-tool';
import { PhotoService } from './photo.service';
import { FieldSync } from './sync/field-sync.service';
import { InspectionRequest } from './field.api';
import { building, buildingField, settle, snapshot, syncTesting } from './sync/testing';

@Component({ selector: 'app-load-tool', template: '' })
class LoadToolStub {
  readonly buildingId = input<string>();
  readonly buildingType = input<string>();
  readonly form = input<unknown>();
  readonly existing = input<unknown>();
  readonly openSync = output<void>();
}

/** The panel for one building of a project opened offline from the device. */
async function setup(props: Parameters<typeof building>[1] = {}) {
  const t = syncTesting(false);
  await t.db.putSnapshot(snapshot({ buildings: { type: 'FeatureCollection', features: [building('b1', { erf: '5009', version: 7, ...props })] }, photoCounts: { b1: 1 } }));
  TestBed.configureTestingModule({
    imports: [BuildingPanel],
    providers: [...t.providers, { provide: PhotoService, useValue: { prepare: async (f: File) => f } }],
  }).overrideComponent(BuildingPanel, { remove: { imports: [LoadTool] }, add: { imports: [LoadToolStub] } });
  const sync = TestBed.inject(FieldSync);
  await sync.open('p1');
  const fixture = TestBed.createComponent(BuildingPanel);
  const f = sync.view()!.buildings.features[0];
  fixture.componentRef.setInput('building', { id: f.id, props: f.properties });
  fixture.componentRef.setInput('gps', { lon: 28.1, lat: -25.52, accuracyM: 4 });
  let opened = 0;
  fixture.componentInstance.openSync.subscribe(() => opened++);
  await fixture.whenStable();
  const refresh = async () => {
    const g = sync.view()!.buildings.features[0];
    fixture.componentRef.setInput('building', { id: g.id, props: g.properties });
    await settle();
    await fixture.whenStable();
  };
  return { ...t, sync, fixture, refresh, opened: () => opened, el: fixture.nativeElement as HTMLElement };
}

const button = (el: HTMLElement, text: string) => [...el.querySelectorAll('button')].find((b) => b.textContent?.trim().includes(text))!;

describe('BuildingPanel', () => {
  it('confirms the predicted type in one tap with GPS, notes and the version seen, offline', async () => {
    const { db, el, refresh } = await setup();
    expect(el.textContent).toContain('Erf 5009');
    expect(el.textContent).toContain('Predicted house');

    const notes = el.querySelector('textarea')!;
    notes.value = 'Shack at the back';
    notes.dispatchEvent(new Event('input'));
    await refresh();
    button(el, 'Confirm house').click();
    await refresh();

    const [op] = await db.ops();
    const req = (op.body as { req: InspectionRequest }).req;
    expect(req).toMatchObject({ action: 'confirm', type: 'house', version: 7, notes: 'Shack at the back', position: { lon: 28.1, lat: -25.52, accuracyM: 4 } });
    expect(req.inspectionId).toMatch(/^[0-9a-f-]{36}$/);
    expect(el.textContent).toContain('Confirmed');
    expect(el.textContent).toContain('Saved on this tablet');
    expect(el.querySelector('textarea')!.value).toBe('');
  });

  it('corrects the type and marks not present', async () => {
    const { db, el, refresh } = await setup();
    button(el, 'school').click();
    await refresh();
    button(el, 'Not present').click();
    await refresh();
    expect((await db.ops()).map((o) => (o.body as { req: InspectionRequest }).req)).toMatchObject([
      { action: 'correct', type: 'school' },
      { action: 'not_present' },
    ]);
    expect(el.querySelector('app-load-tool')).toBeNull();
  });

  it('shows a held change and asks the person to decide', async () => {
    const { db, el, refresh, opened } = await setup();
    const op = await db.addOp({
      id: 'o1', projectId: 'p1', state: 'conflict', createdAt: '', server: buildingField('b1', { effectiveType: 'shop' }),
      body: { kind: 'inspect', buildingId: 'b1', req: { inspectionId: 'i', action: 'confirm', type: 'house', capturedAt: '', version: 7 } },
    });
    TestBed.inject(FieldSync).ops.set([op]);
    await refresh();
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('changed on the server');
    button(el, 'Decide').click();
    expect(opened()).toBe(1);
  });

  it('queues a photo against the building', async () => {
    const { db, el, refresh } = await setup();
    expect(el.textContent).toContain('1 photo');
    const input = el.querySelector<HTMLInputElement>('input[type="file"]')!;
    Object.defineProperty(input, 'files', { value: [new File([new Uint8Array([0xff, 0xd8])], 'p.jpg', { type: 'image/jpeg' })], configurable: true });
    input.dispatchEvent(new Event('change'));
    await refresh();
    expect((await db.ops())[0].body).toMatchObject({ kind: 'photo', buildingId: 'b1', contentType: 'image/jpeg' });
    expect(el.textContent).toContain('2 photos');
    expect(el.textContent).toContain('1 waiting to upload');
  });

  it('hides the load tool for a building that is not present', async () => {
    const { el } = await setup({ status: 'notpresent' });
    expect(el.querySelector('app-load-tool')).toBeNull();
    expect(button(el, 'Not present').disabled).toBe(true);
  });
});
