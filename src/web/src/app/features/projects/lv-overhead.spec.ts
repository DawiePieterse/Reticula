import { TestBed } from '@angular/core/testing';
import { LvOverhead, LvSupportResult, lvLayers } from './lv-network.api';
import { network } from './lv-network.testing';
import { LvOverheadSection, MAX_SUPPORT_ROWS } from './lv-overhead';

function pole(id: string, over: Partial<LvSupportResult> = {}): LvSupportResult {
  return {
    id, label: id.replace('N', 'P'), kind: 'pole', marked: true, role: 'terminal', spans: 1, deviationDeg: null, loadKn: 4.6, governingCase: 'cold',
    poleClass: '9m-140', stay: true, stayTensionKn: 6.5, passes: true, coordinates: [28.1, -25.52], ...over,
  };
}

export function overhead(): LvOverhead {
  return {
    rulesRef: 'eskom/0.7.0', rulesHash: '192d2d359d22ae23', clause: 'LV overhead line settings: placeholders',
    summary: { spans: 3, longestSpanM: 61.4, sections: 1, supports: 4, polesNeeded: 1, stays: 2, poleClasses: { '9m-140': 3 } },
    spans: [
      { id: 'S1', fromNode: 'N1', toNode: 'N2', fromLabel: 'TX1', toLabel: 'N2', branches: ['B3'], feeder: null, conductor: 'ABC-3C-70', lengthM: 20,
        bendM: 0, section: 'T1', sagM: 0.2, clearanceM: 7, tensionKn: 5.1, passes: true, coordinates: [[28.1, -25.52], [28.1, -25.5198]] },
      { id: 'S2', fromNode: 'N2', toNode: 'N3', fromLabel: 'N2', toLabel: 'P3', branches: ['B1'], feeder: 'TX1-F1', conductor: 'ABC-3C-70', lengthM: 61.4,
        bendM: 0, section: 'T1', sagM: 2.1, clearanceM: 5.1, tensionKn: 5.1, passes: false, coordinates: [[28.1, -25.5198], [28.106, -25.5198]] },
    ],
    supports: [
      pole('N1', { label: 'TX1', kind: 'source', poleClass: null, loadKn: 5.2 }),
      pole('N2', { label: 'N2', kind: 'junction', marked: false, role: 'junction', spans: 3, loadKn: 1.3, stay: false, stayTensionKn: null, passes: false }),
      pole('N3'),
      pole('N4', { role: 'intermediate', spans: 2, deviationDeg: 1, loadKn: 0.95, stay: false, stayTensionKn: null, governingCase: 'wind' }),
    ],
    sections: [{ id: 'T1', spans: ['S1', 'S2'], conductor: 'ABC-3C-70', rulingSpanM: 52.3, tensionKn: { everyday: 4.005, hot: 1.9, cold: 5.1, wind: 5.05 }, governing: 'everyday', maxPullKn: 8.9, passes: true }],
    issues: [],
    lowestClearance: null, highestPoleLoad: null,
    placeholders: ['the Eskom overhead line settings'],
  };
}

async function setup(o: LvOverhead) {
  TestBed.configureTestingModule({ imports: [LvOverheadSection] });
  const fixture = TestBed.createComponent(LvOverheadSection);
  fixture.componentRef.setInput('overhead', o);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture.nativeElement as HTMLElement;
}

const rows = (el: HTMLElement, sel: string) =>
  [...el.querySelectorAll(`${sel} tbody tr`)].map((tr) => [...tr.querySelectorAll('td')].map((td) => td.textContent?.replace(/\s+/g, ' ').trim()));

describe('LvOverheadSection', () => {
  it('shows the sections and the poles that carry line tension, heaviest first', async () => {
    const el = await setup(overhead());
    expect(el.querySelector('[aria-label="Overhead placeholder inputs"]')!.textContent).toContain('the Eskom overhead line settings');
    const chips = el.querySelector('ul.chips')!.textContent!.replace(/\s+/g, ' ');
    expect(chips).toContain('3 spans, longest 61 m');
    expect(chips).toContain('1 poles to mark');
    expect(chips).toContain('3 × 9m-140');
    expect(rows(el, '.sections')).toEqual([['T1', 'ABC-3C-70', '2', '52.3 m', 'everyday tension', '4.01 kN', '1.90 kN', '5.10 kN', '5.05 kN', '8.9 kN', 'Passes']]);
    expect(rows(el, '.supports')).toEqual([
      ['TX1', 'Terminal', '–', '5.20 kN cold', 'transformer', '6.5 kN'],
      ['P3', 'Terminal', '–', '4.60 kN cold', '9m-140', '6.5 kN'],
      ['N2 (not marked)', 'Junction', '–', '1.30 kN cold', '9m-140', ''],
    ]);
  });

  it('lists at most twenty poles', async () => {
    const o = overhead();
    o.supports = Array.from({ length: MAX_SUPPORT_ROWS + 3 }, (_, i) => pole(`N${i + 1}`, { loadKn: i }));
    const el = await setup(o);
    expect(el.querySelectorAll('.supports tbody tr')).toHaveLength(MAX_SUPPORT_ROWS);
    expect(el.textContent).toContain('and 3 more poles');
  });
});

describe('lvLayers overhead', () => {
  it('draws spans that fail and rings stayed supports', () => {
    const l = lvLayers(network({ overhead: overhead() }));
    expect(l.spans.features.map((f) => [f.id, f.properties.label, f.properties.failing])).toEqual([['S1', 'TX1–N2', false], ['S2', 'N2–P3', true]]);
    expect(l.nodes.features.map((f) => f.properties.stay)).toEqual([true, false]);
    expect(lvLayers(network()).spans.features).toEqual([]);
  });
});
