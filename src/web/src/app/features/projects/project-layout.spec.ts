import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BuildingProps, Feature, ImportResponse } from './layout.api';
import { LayoutLayers, ProjectLayout } from './project-layout';

const SQUARE = { type: 'Polygon' as const, coordinates: [[[28.1, -25.52], [28.11, -25.52], [28.11, -25.51], [28.1, -25.52]] as [number, number][]] };

const building = (id: string, confidence: number, low: boolean, erf: string): Feature<BuildingProps> => ({
  type: 'Feature', id, geometry: SQUARE,
  properties: { predictedType: 'house', confidence, source: 'footprint:80 m²', lowConfidence: low, status: 'predicted', confirmedType: null, effectiveType: 'house', areaM2: 80, erf, zoning: null, signals: [], version: 1 },
});

const summary = { stands: 2, standsWithoutErf: 0, buildings: 3, lowConfidence: 2, inspected: 0, predictedByType: { house: 3 } };

const preview = (issues: ImportResponse['issues'], featureCount = 2): ImportResponse => ({
  batchId: null, committed: false, format: 'kml', sourceCrs: 'WGS84', crsReason: 'KML is always longitude/latitude',
  featureCount, issues, layers: [], preview: { type: 'FeatureCollection', features: [] },
});

/** Lets awaited HTTP promises resolve, then change detection run. */
async function settle(fixture: { whenStable(): Promise<unknown> }) {
  await new Promise((r) => setTimeout(r));
  await fixture.whenStable();
}

async function setup(canEdit = true) {
  TestBed.configureTestingModule({ imports: [ProjectLayout], providers: [provideHttpClient(), provideHttpClientTesting()] });
  const fixture = TestBed.createComponent(ProjectLayout);
  fixture.componentRef.setInput('projectId', 'p1');
  fixture.componentRef.setInput('canEdit', canEdit);
  const layers: LayoutLayers[] = [];
  const focused: string[] = [];
  fixture.componentInstance.layersChange.subscribe((l) => layers.push(l));
  fixture.componentInstance.focus.subscribe((id) => focused.push(id));
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  const flushLayout = () => {
    http.expectOne('/api/projects/p1/layout-summary').flush(summary);
    http.expectOne('/api/projects/p1/stands').flush({ type: 'FeatureCollection', features: [] });
    http.expectOne('/api/projects/p1/buildings').flush({
      type: 'FeatureCollection',
      features: [building('b-ok', 0.9, false, '3'), building('b-low2', 0.45, true, '2'), building('b-low1', 0.3, true, '1')],
    });
  };
  flushLayout();
  await settle(fixture);
  return { fixture, http, layers, focused, flushLayout, el: fixture.nativeElement as HTMLElement };
}

function chooseFile(el: HTMLElement, name = 'layout.kml') {
  const input = el.querySelector<HTMLInputElement>('input[type="file"]')!;
  Object.defineProperty(input, 'files', { value: [new File(['<kml/>'], name)] });
  input.dispatchEvent(new Event('change'));
}

const button = (el: HTMLElement, text: string) => [...el.querySelectorAll('button')].find((b) => b.textContent?.includes(text))!;

describe('ProjectLayout', () => {
  it('lists low-confidence buildings lowest first and focuses on click', async () => {
    const { el, focused } = await setup();
    const rows = [...el.querySelectorAll('table.low tbody tr')];
    expect(rows.map((r) => r.querySelector('td')?.textContent)).toEqual(['1', '2']);
    (rows[0] as HTMLElement).click();
    expect(focused).toEqual(['b-low1']);
    expect(el.textContent).toContain('2 low confidence');
  });

  it('checks a file, shows issues, then imports and reloads', async () => {
    const { fixture, http, el, layers, flushLayout } = await setup();
    chooseFile(el);
    await fixture.whenStable();

    button(el, 'Check file').click();
    const check = http.expectOne('/api/projects/p1/imports');
    expect((check.request.body as FormData).get('dryRun')).toBe('true');
    expect((check.request.body as FormData).get('kind')).toBe('stands');
    check.flush(preview([{ severity: 'warning', code: 'erf_missing', message: 'Some stands have no erf number.', count: 3, samples: ['pm-1'] }]));
    await settle(fixture);
    expect(el.textContent).toContain('Some stands have no erf number.');
    expect(layers.at(-1)?.preview).not.toBeNull();

    const importButton = button(el, 'Import 2 stands');
    expect(importButton.disabled).toBe(false);
    importButton.click();
    const commit = http.expectOne('/api/projects/p1/imports');
    expect((commit.request.body as FormData).get('dryRun')).toBe('false');
    commit.flush({ ...preview([]), committed: true, batchId: 'b1' });
    await settle(fixture);
    flushLayout();
    await settle(fixture);
    expect(el.textContent).toContain('Imported 2 stands.');
    expect(layers.at(-1)?.preview).toBeNull();
  });

  it('blocks import when the file has errors', async () => {
    const { fixture, http, el } = await setup();
    chooseFile(el);
    await fixture.whenStable();
    button(el, 'Check file').click();
    http.expectOne('/api/projects/p1/imports').flush(
      preview([{ severity: 'error', code: 'outside_area', message: 'No features fall inside the project area.', count: 2, samples: [] }]),
    );
    await settle(fixture);
    expect(el.querySelector('.issues .error')?.textContent).toContain('inside the project area');
    expect(button(el, 'Import').disabled).toBe(true);
  });

  it('shows the reason for an unreadable file', async () => {
    const { fixture, http, el } = await setup();
    chooseFile(el, 'layout.dwg');
    await fixture.whenStable();
    button(el, 'Check file').click();
    http.expectOne('/api/projects/p1/imports').flush(
      { title: 'Validation', errors: { file: ['Unsupported file type for layout.dwg'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(fixture);
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Unsupported file type');
  });

  it('hides the import panel from inspectors', async () => {
    const { el } = await setup(false);
    expect(el.querySelector('fieldset.import')).toBeNull();
    expect(el.querySelector('table.low')).not.toBeNull();
  });
});
