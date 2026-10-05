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

const summary = { stands: 2, standsWithoutErf: 0, buildings: 3, lowConfidence: 2, inspected: 0, predictedByType: { house: 3 }, roads: 0, contours: 0, networkAssets: 0, networkIncomplete: 0 };
const empty = { type: 'FeatureCollection', features: [] };

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
  const flushLayout = (over: Partial<typeof summary> = {}, network: object = empty) => {
    http.expectOne('/api/projects/p1/layout-summary').flush({ ...summary, ...over });
    http.expectOne('/api/projects/p1/roads').flush(empty);
    http.expectOne('/api/projects/p1/contours').flush(empty);
    http.expectOne('/api/projects/p1/network').flush(network);
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

  it('fetches roads from OpenStreetMap without a file, then imports them', async () => {
    const { fixture, http, el, flushLayout } = await setup();
    const kind = el.querySelector<HTMLSelectElement>('select[name="kind"]')!;
    kind.value = 'roads';
    kind.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(el.querySelector('input[type="file"]')!.getAttribute('accept')).toContain('.zip');

    button(el, 'Fetch from OpenStreetMap').click();
    const check = http.expectOne('/api/projects/p1/imports');
    const form = check.request.body as FormData;
    expect([form.get('source'), form.get('kind'), form.get('dryRun'), form.get('file')]).toEqual(['osm', 'roads', 'true', null]);
    check.flush({ ...preview([], 12), format: 'overpass' });
    await settle(fixture);
    expect(el.textContent).toContain('12 roads found in OpenStreetMap');

    button(el, 'Import 12 roads').click();
    const commit = http.expectOne('/api/projects/p1/imports');
    expect((commit.request.body as FormData).get('source')).toBe('osm');
    commit.flush({ ...preview([], 12), committed: true, batchId: 'b1' });
    await settle(fixture);
    flushLayout({ roads: 12 });
    await settle(fixture);
    expect(el.textContent).toContain('12 roads');
  });

  it('offers OpenStreetMap only for buildings and roads, and CSV for the existing network', async () => {
    const { fixture, el } = await setup();
    const kind = el.querySelector<HTMLSelectElement>('select[name="kind"]')!;
    expect(button(el, 'Fetch from OpenStreetMap')).toBeUndefined();
    kind.value = 'network';
    kind.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(button(el, 'Fetch from OpenStreetMap')).toBeUndefined();
    expect(el.querySelector('input[type="file"]')!.getAttribute('accept')).toContain('.csv');
    expect(el.textContent).toContain('CSV with lon and lat columns');
  });

  it('counts the existing network and passes it to the map', async () => {
    TestBed.resetTestingModule();
    const { fixture, http, layers } = await (async () => {
      TestBed.configureTestingModule({ imports: [ProjectLayout], providers: [provideHttpClient(), provideHttpClientTesting()] });
      const f = TestBed.createComponent(ProjectLayout);
      f.componentRef.setInput('projectId', 'p1');
      const got: LayoutLayers[] = [];
      f.componentInstance.layersChange.subscribe((l) => got.push(l));
      f.detectChanges();
      return { fixture: f, http: TestBed.inject(HttpTestingController), layers: got };
    })();
    const net = { type: 'FeatureCollection', features: [{ type: 'Feature', id: 'n1', geometry: { type: 'Point', coordinates: [28.1, -25.52] },
      properties: { assetType: 'transformer', label: 'TRF 1', voltageKv: null, ratingKva: null, capacityKva: null, faultLevelKa: null, missing: ['rating_kva'] } }] };
    http.expectOne('/api/projects/p1/layout-summary').flush({ ...summary, networkAssets: 1, networkIncomplete: 1 });
    http.expectOne('/api/projects/p1/stands').flush(empty);
    http.expectOne('/api/projects/p1/buildings').flush(empty);
    http.expectOne('/api/projects/p1/roads').flush(empty);
    http.expectOne('/api/projects/p1/contours').flush(empty);
    http.expectOne('/api/projects/p1/network').flush(net);
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('1 existing network (1 incomplete)');
    expect(layers.at(-1)!.network!.features[0].properties.label).toBe('TRF 1');
  });
});
