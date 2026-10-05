import { LvAnalysis, LvLoads, LvNetwork } from './lv-network.api';

/** Test data for the LV network specs. */
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

export function analysis(): LvAnalysis {
  const drop = (R: number, W: number, B: number) => ({ R, W, B });
  return {
    rulesRef: 'eskom/0.6.0', rulesHash: '9f1c2b7a00d4e5f6', clause: 'LV design check settings: placeholders', limitPct: 10, phaseVoltageV: 230, confidencePct: 90,
    points: [
      { id: 'N2', kind: 'node', feeder: null, distanceM: 20, dropPct: drop(0.4, 0.3, 0.3), worstPct: 0.4, faultA: 2400, passes: true },
      { id: 'N3', kind: 'node', feeder: 'TX1-F1', distanceM: 620, dropPct: drop(8.6, 7.1, 6.0), worstPct: 8.6, faultA: 610, passes: true },
      { id: 'N4', kind: 'node', feeder: 'TX1-F2', distanceM: 670, dropPct: drop(11.2, 9.0, 8.1), worstPct: 11.2, faultA: 540, passes: false },
    ],
    branches: [
      { id: 'B1', feeder: 'TX1-F1', conductor: 'ABC-3C-70', ratingA: 191, currentA: drop(60, 52, 41), utilisationPct: 31.4, passes: true },
      { id: 'B2', feeder: 'TX1-F2', conductor: 'ABC-3C-70', ratingA: 191, currentA: drop(201, 150, 120), utilisationPct: 105.2, passes: false },
      { id: 'B3', feeder: null, conductor: 'ABC-3C-70', ratingA: 191, currentA: drop(150, 140, 130), utilisationPct: 78.5, passes: true },
    ],
    feeders: [
      { feeder: 'TX1-F1', maxDropPct: 8.6, maxDropAt: 'N3', maxUtilisationPct: 31.4, maxUtilisationBranch: 'B1', minFaultA: 610, minFaultAt: 'N3', passes: true },
      { feeder: 'TX1-F2', maxDropPct: 11.2, maxDropAt: 'N4', maxUtilisationPct: 105.2, maxUtilisationBranch: 'B2', minFaultA: 540, minFaultAt: 'N4', passes: false },
    ],
    issues: [],
    worstDrop: null, worstCurrent: null, lowestFault: null,
    placeholders: ['Source transformer: 100 kVA, 4 % impedance'],
  };
}
