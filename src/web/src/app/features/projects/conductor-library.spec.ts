import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ConductorLibrary } from './conductor-library';
import { Conductor } from './lv-network.api';

const cable: Conductor = {
  code: 'CU-4C-70', description: '70 mm² Cu 4-core PVC/SWA/PVC 600/1000 V', kind: 'underground', material: 'cu', sizeMm2: 70, cores: 4,
  uses: ['feeder'], rOhmPerKm: 0.268, rAcOhmPerKm: null, rAcTempC: null, xOhmPerKm: 0.08, ratingA: 210, ratingsA: { ground: 210, pipe: 171, air: 205 }, faultK: 0.115,
  oneSecondKa: 8.05, placeholder: ['r_ohm_per_km', 'x_ohm_per_km'], clause: 'R placeholder', ratingClause: 'Eskom 240-56030637 Rev 2 Table 6',
  index: 'ESKOM-LVCABLE-RATING',
};
const abc1c: Conductor = {
  ...cable, code: 'ABC-1C-70', kind: 'overhead', material: 'al', cores: 2, uses: ['feeder', 'service'], rOhmPerKm: 0.443, rAcOhmPerKm: 0.568,
  rAcTempC: 90, xOhmPerKm: 0.083, ratingA: 213, ratingsA: { air: 213 }, faultK: 0.09429, oneSecondKa: 6.6, placeholder: [],
};
const abc: Conductor = {
  ...cable, code: 'ABC-3C-70', kind: 'overhead', material: 'al', cores: null, ratingA: 200, ratingsA: {}, faultK: null, oneSecondKa: null,
  placeholder: ['r_ohm_per_km', 'x_ohm_per_km', 'rating_a'], ratingClause: '',
};

async function setup() {
  TestBed.configureTestingModule({ imports: [ConductorLibrary], providers: [provideHttpClient(), provideHttpClientTesting()] });
  const fixture = TestBed.createComponent(ConductorLibrary);
  fixture.componentRef.setInput('projectId', 'p1');
  fixture.detectChanges();
  await fixture.whenStable();
  const stable = async () => {
    await new Promise((r) => setTimeout(r));
    await fixture.whenStable();
  };
  return { fixture, stable, http: TestBed.inject(HttpTestingController), el: fixture.nativeElement as HTMLElement };
}

describe('ConductorLibrary', () => {
  it('loads when opened and marks placeholder values', async () => {
    const { el, http, stable } = await setup();
    http.expectNone('/api/projects/p1/conductors');
    const details = el.querySelector('details')!;
    details.open = true;
    details.dispatchEvent(new Event('toggle'));
    await stable();
    http.expectOne('/api/projects/p1/conductors').flush({ rulesRef: 'eskom/0.5.0', rulesHash: 'd6a9', conductors: [cable, abc, abc1c] });
    await stable();
    expect(el.querySelector('summary')?.textContent).toContain('(3, rules eskom/0.5.0)');
    const rows = [...el.querySelectorAll('tbody tr')];
    const cells = (r: Element) => [...r.querySelectorAll('td')].map((td) => td.textContent?.trim());
    expect(cells(rows[0])).toEqual(['CU-4C-70', 'feeder', '210 A', '171 A', '205 A', '0.268', '—', '0.080', '8.05 kA']);
    // Ratings come from the standard; impedances are placeholders.
    expect([...rows[0].querySelectorAll('td.placeholder')].map((td) => td.textContent?.trim())).toEqual(['0.268', '0.080']);
    expect(cells(rows[1])).toEqual(['ABC-3C-70', 'feeder', '200 A in air (overhead)', '0.268', '—', '0.080', '—']);
    expect(rows[1].querySelectorAll('td.placeholder')).toHaveLength(3);
    expect(rows[0].getAttribute('title')).toContain('Table 6');
    // Manufacturer data for single-phase ABC: AC resistance at 90 °C, nothing a placeholder.
    expect(cells(rows[2])).toEqual(['ABC-1C-70', 'feeder, service', '213 A in air (overhead)', '0.443', '0.568 at 90 °C', '0.083', '6.60 kA']);
    expect(rows[2].querySelectorAll('td.placeholder')).toHaveLength(0);
  });
});
