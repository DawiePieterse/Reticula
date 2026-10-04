import { Component, input, output } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BuildingProps } from '../projects/layout.api';
import { BuildingPanel, SelectedBuilding } from './building-panel';
import { BuildingField } from './field.api';
import { LoadTool } from './load-tool';
import { PhotoService } from './photo.service';
import { idle } from '../../../testing/idle';

@Component({ selector: 'app-load-tool', template: '' })
class LoadToolStub {
  readonly projectId = input<string>();
  readonly buildingId = input<string>();
  readonly buildingType = input<string>();
  readonly form = input<unknown>();
  readonly existing = input<unknown>();
  readonly label = input<string>();
  readonly saved = output<unknown>();
}

const props = (over: Partial<BuildingProps> = {}): BuildingProps => ({
  predictedType: 'house', confidence: 0.55, source: 'footprint:72 m²', lowConfidence: true, status: 'predicted',
  confirmedType: null, effectiveType: 'house', areaM2: 72, erf: '5009', zoning: null, signals: [], version: 7, ...over,
});

const field = (over: Partial<BuildingField> = {}): BuildingField => ({
  id: 'b1', status: 'confirmed', predictedType: 'house', confirmedType: 'house', effectiveType: 'house', confidence: 0.55,
  erf: '5009', location: { type: 'Point', coordinates: [28.1, -25.52] }, inspectedAt: null, version: 8, ...over,
});

async function setup(building: SelectedBuilding = { id: 'b1', props: props() }) {
  TestBed.configureTestingModule({
    imports: [BuildingPanel],
    providers: [provideHttpClient(), provideHttpClientTesting(), { provide: PhotoService, useValue: { prepare: async (f: File) => f } }],
  }).overrideComponent(BuildingPanel, { remove: { imports: [LoadTool] }, add: { imports: [LoadToolStub] } });
  const fixture = TestBed.createComponent(BuildingPanel);
  fixture.componentRef.setInput('projectId', 'p1');
  fixture.componentRef.setInput('building', building);
  fixture.componentRef.setInput('gps', { lon: 28.1, lat: -25.52, accuracyM: 4 });
  const changed: BuildingField[] = [];
  fixture.componentInstance.changed.subscribe((b) => changed.push(b));
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  await idle();
  http.expectOne('/api/projects/p1/photos?buildingId=b1').flush([]);
  await fixture.whenStable();
  return { fixture, http, changed, el: fixture.nativeElement as HTMLElement };
}

const settle = async (f: { whenStable(): Promise<unknown> }) => {
  await new Promise((r) => setTimeout(r));
  await f.whenStable();
};
const button = (el: HTMLElement, text: string) => [...el.querySelectorAll('button')].find((b) => b.textContent?.trim().includes(text))!;

describe('BuildingPanel', () => {
  it('confirms the predicted type in one tap with GPS, notes and the version seen', async () => {
    const { fixture, http, changed, el } = await setup();
    expect(el.textContent).toContain('Erf 5009');
    expect(el.textContent).toContain('Predicted house');

    const notes = el.querySelector('textarea')!;
    notes.value = 'Shack at the back';
    notes.dispatchEvent(new Event('input'));
    button(el, 'Confirm house').click();

    await idle();
    const req = http.expectOne({ method: 'PUT', url: '/api/projects/p1/buildings/b1/inspection' });
    expect(req.request.body).toMatchObject({ action: 'confirm', type: 'house', version: 7, notes: 'Shack at the back', position: { lon: 28.1, lat: -25.52, accuracyM: 4 } });
    expect(req.request.body.inspectionId).toMatch(/^[0-9a-f-]{36}$/);
    req.flush(field());
    await settle(fixture);
    expect(changed[0].status).toBe('confirmed');
  });

  it('corrects the type and marks not present', async () => {
    const { fixture, http, el } = await setup();
    button(el, 'school').click();
    await idle();
    const correct = http.expectOne('/api/projects/p1/buildings/b1/inspection');
    expect(correct.request.body).toMatchObject({ action: 'correct', type: 'school' });
    correct.flush(field({ confirmedType: 'school', effectiveType: 'school' }));
    await settle(fixture);
    button(el, 'Not present').click();
    await idle();
    expect(http.expectOne('/api/projects/p1/buildings/b1/inspection').request.body).toMatchObject({ action: 'not_present' });
  });

  it('shows a conflict without overwriting and passes the current state up', async () => {
    const { fixture, http, changed, el } = await setup();
    button(el, 'Confirm house').click();
    await idle();
    http.expectOne('/api/projects/p1/buildings/b1/inspection').flush(field({ effectiveType: 'shop', confirmedType: 'shop' }), { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Someone else updated this building');
    expect(changed[0].effectiveType).toBe('shop');
  });

  it('uploads a photo against the building', async () => {
    const { fixture, http, el } = await setup();
    const input = el.querySelector<HTMLInputElement>('input[type="file"]')!;
    Object.defineProperty(input, 'files', { value: [new File([new Uint8Array([0xff, 0xd8])], 'p.jpg', { type: 'image/jpeg' })], configurable: true });
    input.dispatchEvent(new Event('change'));
    await settle(fixture);
    await idle();
    const req = http.expectOne({ method: 'POST', url: '/api/projects/p1/photos' });
    expect((req.request.body as FormData).get('buildingId')).toBe('b1');
    req.flush({ id: 'x', buildingId: 'b1', candidateId: null, contentType: 'image/jpeg', sizeBytes: 2, capturedAt: '' });
    await settle(fixture);
    expect(el.textContent).toContain('1 photo');
  });

  it('hides the load tool for a building that is not present', async () => {
    const { el } = await setup({ id: 'b1', props: props({ status: 'notpresent' }) });
    expect(el.querySelector('app-load-tool')).toBeNull();
    expect(button(el, 'Not present').disabled).toBe(true);
  });
});
