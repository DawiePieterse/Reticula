import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { DROP_COLOURS, FEEDER_COLOURS, LvLayers, LvNetwork, PHASE_COLOURS, UNFED_COLOUR, lvLayers } from './lv-network.api';
import { analysis, loads, network } from './lv-network.testing';
import { LvNetworkPanel } from './lv-network-panel';

const URL = '/api/projects/p1/lv-network';

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
    expect(el.textContent).not.toContain('Checks');
  });

  it('shows the checks per feeder and warns about placeholders', async () => {
    const { el } = await setup(true, network({ analysis: analysis() }));
    expect(el.querySelector('[aria-label="Placeholder inputs"]')!.textContent).toContain('Source transformer: 100 kVA, 4 % impedance');
    const rows = [...el.querySelectorAll('.checks tbody tr')].map((tr) => [...tr.querySelectorAll('td')].map((td) => td.textContent?.replace(/\s+/g, ' ').trim()));
    expect(rows).toEqual([
      ['TX1-F1', '8.60 % at N3', '31 % on B1', '610 A at N3', 'Passes'],
      ['TX1-F2', '11.20 % at N4', '105 % on B2', '540 A at N4', 'Fails'],
      ['Source links', '0.40 % at N2', '79 % on B3', '2,400 A at N2', 'Passes'],
    ]);
    expect(el.querySelectorAll('.checks tbody tr')[1].querySelector('td:nth-child(2)')!.classList).toContain('warn');
    expect(el.textContent!.replace(/\s+/g, ' ')).toContain('against a limit of 10 % of 230 V');
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

  it('marks overloaded branches and bands nodes by voltage drop', () => {
    const l = lvLayers(network({ analysis: analysis() }));
    expect(l.branches.features.map((f) => f.properties.overloaded)).toEqual([false, true, false]);
    expect(l.nodes.features.map((f) => f.properties.band)).toEqual([null, 'ok']);
    const nodes = network().nodes;
    const more = lvLayers(network({ analysis: analysis(), nodes: [...nodes, { ...nodes[1], id: 'N3' }, { ...nodes[1], id: 'N4' }] }));
    expect(more.nodes.features.map((f) => f.properties.dropColour)).toEqual([null, DROP_COLOURS.ok, DROP_COLOURS.near, DROP_COLOURS.over]);
  });
});
