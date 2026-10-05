import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { JobsService } from '../../core/jobs/jobs.service';
import { RooftopImagery } from './rooftop-imagery';
import { idle } from '../../../testing/idle';

const ortho = (over: object = {}) => ({
  id: 'i1', source: 'orthophoto', format: 'geotiff', label: 'NGI 2023 0.25 m', licence: 'CD:NGI aerial imagery licence', status: 'ready', active: true,
  sizeBytes: 1000, error: null, model: null, classifiedAt: null, createdAt: '2026-10-05T10:00:00Z', ...over,
});
const model = { model: { used: true, reason: 'cross-validated accuracy 91% on 44 confirmed buildings', trained_on: 44, types: { house: 34, shop: 10 }, left_out_types: ['school'],
  accuracy: 0.91, min_accuracy: 0.75 }, signals: 120, outside_imagery: 3, gsd_m: 0.25 };

describe('RooftopImagery', () => {
  async function setup(index: object) {
    TestBed.configureTestingModule({
      imports: [RooftopImagery],
      providers: [provideHttpClient(), provideHttpClientTesting(),
        { provide: JobsService, useValue: { watch: (id: string) => of({ id, kind: 'imagery.classify', status: 'succeeded', progressPct: 100, message: null, error: null, startedAt: null, finishedAt: null }) } }],
    });
    const fixture = TestBed.createComponent(RooftopImagery);
    fixture.componentRef.setInput('projectId', 'p1');
    fixture.componentRef.setInput('canEdit', true);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await idle();
    http.expectOne('/api/projects/p1/imagery').flush(index);
    await idle();
    await fixture.whenStable();
    return { fixture, http, el: fixture.nativeElement as HTMLElement };
  }

  const button = (el: HTMLElement, text: string) => [...el.querySelectorAll('button')].find((b) => b.textContent?.includes(text))!;

  it('uploads an orthophoto only with a licence and the declaration', async () => {
    const { fixture, http, el } = await setup({ items: [], google: { available: false, licence: null, zoom: 19 } });
    expect(el.textContent).toContain("Google's standard terms do not allow deriving data");
    expect(button(el, 'Run the rooftop classifier').disabled).toBe(true);
    const input = el.querySelector<HTMLInputElement>('input[name="orthoFile"]')!;
    Object.defineProperty(input, 'files', { value: [new File([new Uint8Array([0x49, 0x49, 0x2a, 0])], 'ortho.tif', { type: 'image/tiff' })] });
    input.dispatchEvent(new Event('change'));
    for (const [name, value] of [['orthoLabel', 'NGI 2023 0.25 m'], ['orthoLicence', 'CD:NGI aerial imagery licence']]) {
      const i = el.querySelector<HTMLInputElement>(`input[name="${name}"]`)!;
      i.value = value;
      i.dispatchEvent(new Event('input'));
    }
    await fixture.whenStable();
    expect(button(el, 'Upload').disabled).toBe(true);
    el.querySelector<HTMLInputElement>('input[name="orthoDeclaration"]')!.click();
    await fixture.whenStable();
    button(el, 'Upload').click();
    await idle();
    const req = http.expectOne({ method: 'POST', url: '/api/projects/p1/imagery/orthophoto' });
    const body = req.request.body as FormData;
    expect([body.get('label'), body.get('licence'), body.get('declaration')]).toEqual(['NGI 2023 0.25 m', 'CD:NGI aerial imagery licence', 'true']);
    expect((body.get('file') as File).name).toBe('ortho.tif');
    req.flush(ortho());
    await idle();
    http.expectOne('/api/projects/p1/imagery').flush({ items: [ortho()], google: { available: false, licence: null, zoom: 19 } });
    await idle();
    await fixture.whenStable();
    expect(button(el, 'Run the rooftop classifier').disabled).toBe(false);
  });

  it('runs the classifier and shows whether its model is used', async () => {
    const { fixture, http, el } = await setup({ items: [ortho()], google: { available: true, licence: 'GMP-123', zoom: 19 } });
    expect(el.textContent).toContain('under GMP-123');
    button(el, 'Run the rooftop classifier').click();
    await idle();
    http.expectOne({ method: 'POST', url: '/api/projects/p1/imagery/classify' })
      .flush({ imagery: ortho(), job: { id: 'j1', kind: 'imagery.classify', status: 'queued', progressPct: 0, message: null, error: null, startedAt: null, finishedAt: null } });
    await idle();
    http.expectOne('/api/projects/p1/imagery').flush({ items: [ortho({ model, classifiedAt: '2026-10-05T11:00:00Z' })], google: { available: true, licence: 'GMP-123', zoom: 19 } });
    await idle();
    await fixture.whenStable();
    const row = el.querySelector('table.imagery tbody tr')!.textContent!;
    expect(row).toContain('used: cross-validated accuracy 91%');
    expect(row).toContain('120 buildings signalled');
    expect(row).toContain('34 house');
    expect(row).toContain('3 buildings outside the imagery');
  });
});
