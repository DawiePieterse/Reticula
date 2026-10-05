import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { FEEDER_COLOURS, LvLayers, LvLoads, LvNetwork, PHASE_COLOURS, UNFED_COLOUR, lvLayers } from './lv-network.api';
import { LvNetworkPanel } from './lv-network-panel';

const URL = '/api/projects/p1/lv-network';

export function network(over: Partial<LvNetwork> = {}): LvNetwork {
  return {
    id: 'n1', rulesRef: 'eskom/0.3.0', rulesHash: '2b317018e2bda763', clause: 'Reticula LV network drawing tolerances v1 (not a standard)',
    builtAt: '2026-10-05T10:00:00Z', stale: null,
    summary: { routes: 2, sources: 1, sourcesConnected: 1, poles: 0, polesPlaced: 0, feeders: 2, nodes: 4, branches: 3, routeLengthM: 1250, unfedLengthM: 0 },
    feeders: [
      { id: 'TX1-F1', source: 'N1', branches: 1, lengthM: 600, ends: 1, farthestM: 620 },
      { id: 'TX1-F2', source: 'N1', branches: 1, lengthM: 650, ends: 1, farthestM: 670 },
    ],
    issues: [],
    nodes: [
      { id: 'N1', kind: 'source', coordinates: [28.1, -25.52], label: 'TX1', candidateId: 'c1', feeder: null, distanceM: 0 },
      { id: 'N2', kind: 'junction', coordinates: [28.1, -25.5198], label: null, candidateId: null, feeder: null, distanceM: 20 },
    ],
    branches: [
      { id: 'B1', kind: 'route', fromNode: 'N2', toNode: 'N3', coordinates: [[28.1, -25.5198], [28.106, -25.5198]], lengthM: 600, candidateId: 'r1', feeder: 'TX1-F1' },
      { id: 'B2', kind: 'route', fromNode: 'N2', toNode: 'N4', coordinates: [[28.1, -25.5198], [28.094, -25.5198]], lengthM: 650, candidateId: 'r1', feeder: 'TX1-F2' },
      { id: 'B3', kind: 'link', fromNode: 'N1', toNode: 'N2', coordinates: [[28.1, -25.52], [28.1, -25.5198]], lengthM: 20, candidateId: 'c1', feeder: null },
    ],
    loads: null,
    ...over,
  };
}

export function loads(): LvLoads {
  const conn = (id: string, phase: 'R' | 'W' | 'B' | 'RWB' | null, box: string | null) => ({
    loadPointId: id, buildingId: `b-${id}`, label: id, kind: 'residential' as const, kva: 2, branch: 'B1', node: 'N3', offsetM: 600,
    serviceM: 18, box, feeder: phase ? 'TX1-F1' : null, distanceM: phase ? 620 : null, phase,
    service: [[28.1061, -25.5196], [28.106, -25.5198]] as [number, number][],
  });
  return {
    clause: 'Eskom LV service practice as described by the engineer',
    summary: { loads: 10, allocated: 9, unallocated: 0, unestimated: 1, onFeeders: 8, threePhase: 1, boxes: 3, allocatedKva: 33.5, longestServiceM: 37.6 },
    feeders: [{
      feeder: 'TX1-F1', threePhase: 1, unbalancePct: 4.2,
      phases: { R: { customers: 5, kva: 12.5, boxes: 1 }, W: { customers: 4, kva: 10, boxes: 1 }, B: { customers: 3, kva: 9, boxes: 1 } },
    }],
    boxes: [],
    connections: [conn('7001', 'R', 'P1-1'), conn('7002', 'RWB', null), conn('7003', null, 'P4-1')],
  };
}

async function setup(canEdit = true, current: LvNetwork | null = null) {
  TestBed.configureTestingModule({ imports: [LvNetworkPanel], providers: [provideHttpClient(), provideHttpClientTesting()] });
  const fixture = TestBed.createComponent(LvNetworkPanel);
  fixture.componentRef.setInput('projectId', 'p1');
  fixture.componentRef.setInput('canEdit', canEdit);
  const layers: (LvLayers | null)[] = [];
  fixture.componentInstance.layersChange.subscribe((l) => layers.push(l));
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  http.expectOne(URL).flush({ network: current });
  const stable = async () => {
    await new Promise((r) => setTimeout(r));
    await fixture.whenStable();
  };
  await stable();
  return { fixture, http, layers, stable, el: fixture.nativeElement as HTMLElement };
}

const button = (el: HTMLElement) => el.querySelector<HTMLButtonElement>('.page-head button');

describe('LvNetworkPanel', () => {
  it('builds the network and lists its feeders', async () => {
    const { el, http, layers, stable } = await setup();
    expect(el.textContent).toContain('Not built yet');
    expect(layers).toEqual([null]);

    button(el)!.click();
    http.expectOne({ method: 'POST', url: URL }).flush(network());
    await stable();
    expect(el.textContent).toContain('2 routes, 1.25 km');
    expect(el.textContent).toContain('1 of 1 sources connected');
    expect(el.textContent).toContain('The network is radial and every route is fed.');
    expect([...el.querySelectorAll('.feeders tbody tr')].map((r) => [...r.querySelectorAll('td')].map((td) => td.textContent?.trim()))).toEqual([
      ['TX1-F1', '1', '600 m', '1', '620 m'],
      ['TX1-F2', '1', '650 m', '1', '670 m'],
    ]);
    expect(el.textContent).toContain('rules eskom/0.3.0 (2b317018e2bda763)');
    expect(button(el)!.textContent).toContain('Build again');
    expect(layers.at(-1)!.branches.features).toHaveLength(3);
  });

  it('shows what to fix and when it is out of date', async () => {
    const issues: LvNetwork['issues'] = [
      { severity: 'error', code: 'loop', message: 'The LV routes form a loop; the network must be radial.', count: 1, samples: ['B7'], at: [[28.1, -25.51]] },
      { severity: 'warning', code: 'unfed', message: 'Parts of the LV network have no transformer or mini-sub within reach.', count: 2, samples: ['B2', 'B9'], at: [[28.1, -25.5], [28.2, -25.5]] },
    ];
    const { el, layers } = await setup(false, network({
      issues, feeders: [], stale: 'LV routes or sites were marked, moved or removed after the network was built.',
      summary: { ...network().summary, unfedLengthM: 412, feeders: 0 },
    }));
    expect(el.textContent).toContain('Out of date: LV routes or sites were marked, moved or removed after the network was built.');
    expect(el.textContent).not.toContain('Build again to update it');
    expect(button(el)).toBeNull();
    expect(el.querySelector('.issues .error')?.textContent).toContain('Fix: The LV routes form a loop');
    expect(el.querySelector('.issues .warning')?.textContent).toContain('(2) B2, B9');
    expect(el.textContent).toContain('412 m not fed');
    expect(el.textContent).toContain('Feeders are worked out only for parts fed by one source with no loops.');
    expect(el.querySelector('.feeders')).toBeNull();
    expect(layers.at(-1)!.issues.features.map((f) => f.properties.severity)).toEqual(['error', 'warning', 'warning']);
  });

  it('explains when the project rules cannot build a network', async () => {
    const { el, http, stable } = await setup();
    button(el)!.click();
    http.expectOne({ method: 'POST', url: URL }).flush(
      { title: 'One or more validation errors occurred.', errors: { rulesRef: ['rules eskom/0.2.0 has no lv_network section; the LV network needs eskom/0.3.0 or later'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await stable();
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('needs eskom/0.3.0 or later');
  });
});

describe('LvNetworkPanel loads', () => {
  it('shows how loads are connected and spread over the phases', async () => {
    const unallocated = { severity: 'error' as const, code: 'unallocated', message: 'Buildings more than 40 m from the LV network have no service connection.', count: 1, samples: ['7009'], at: [[28.2, -25.5] as [number, number]] };
    const { el, layers } = await setup(true, network({ loads: loads(), issues: [unallocated] }));
    // A load problem is not a feeder problem: the feeders are still worked out.
    expect(el.textContent).not.toContain('Feeders are worked out only');
    const chips = el.querySelectorAll('ul.chips')[1].textContent!.replace(/\s+/g, ' ');
    expect(chips).toContain('9 of 10 buildings connected');
    expect(chips).toContain('3 service boxes');
    expect(chips).toContain('1 three-phase');
    expect(chips).toContain('longest service 38 m');
    expect([...el.querySelectorAll('.phases th')].map((th) => th.textContent?.trim())).toEqual(['Feeder', 'Red', 'White', 'Blue', 'Unbalance']);
    expect([...el.querySelectorAll('.phases tbody td')].map((td) => td.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      'TX1-F1', '5 · 12.5 kVA (1 box)', '4 · 10 kVA (1 box)', '3 · 9 kVA (1 box)', '4.2 %',
    ]);
    expect(el.textContent).toContain('Connected using Eskom LV service practice as described by the engineer.');
    expect(layers.at(-1)!.services.features).toHaveLength(3);
  });

  it('says nothing about loads when the rules have no service settings', async () => {
    const { el } = await setup(true, network());
    expect(el.textContent).not.toContain('Loads and phases');
  });
});

describe('lvLayers', () => {
  it('colours services by phase', () => {
    const l = lvLayers(network({ loads: loads() }));
    expect(l.services.features.map((f) => f.properties.colour)).toEqual([PHASE_COLOURS.R, PHASE_COLOURS.RWB, UNFED_COLOUR]);
    expect(l.loads.features[0]).toMatchObject({ geometry: { type: 'Point', coordinates: [28.1061, -25.5196] }, properties: { box: 'P1-1', label: '7001' } });
    expect(lvLayers(network()).services.features).toEqual([]);
  });

  it('colours branches by feeder and greys out what nothing feeds', () => {
    const n = network();
    const l = lvLayers({ ...n, branches: [...n.branches, { ...n.branches[0], id: 'B4', feeder: null }] });
    expect(l.branches.features.map((f) => f.properties.colour)).toEqual([FEEDER_COLOURS[0], FEEDER_COLOURS[1], UNFED_COLOUR, UNFED_COLOUR]);
    expect(l.nodes.features[0]).toMatchObject({ id: 'N1', geometry: { type: 'Point', coordinates: [28.1, -25.52] }, properties: { kind: 'source', label: 'TX1' } });
  });
});
