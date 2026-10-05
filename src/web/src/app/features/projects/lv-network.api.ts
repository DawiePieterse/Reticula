import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { Position } from './geo';
import { FeatureCollection, GeoJsonLineString, GeoJsonPoint } from './layout.api';

export type LvNodeKind = 'source' | 'pole' | 'junction' | 'joint' | 'end';

export interface LvNode {
  id: string;
  kind: LvNodeKind;
  coordinates: Position;
  /** TX1, MS1 or P1 at a marked site. */
  label: string | null;
  candidateId: string | null;
  feeder: string | null;
  /** Along the network from the source; null outside a radial part fed by one source. */
  distanceM: number | null;
}

export interface LvBranch {
  id: string;
  /** route: part of a marked LV route. link: from a source to the nearest point on its route. */
  kind: 'route' | 'link';
  fromNode: string;
  toNode: string;
  coordinates: Position[];
  lengthM: number;
  candidateId: string | null;
  feeder: string | null;
}

export interface LvFeeder {
  id: string;
  source: string;
  branches: number;
  lengthM: number;
  ends: number;
  farthestM: number;
}

export interface LvIssue {
  severity: 'error' | 'warning';
  code: string;
  message: string;
  count: number;
  samples: string[];
  at: Position[];
}

export interface LvSummary {
  routes: number;
  sources: number;
  sourcesConnected: number;
  poles: number;
  polesPlaced: number;
  feeders: number;
  nodes: number;
  branches: number;
  routeLengthM: number;
  unfedLengthM: number;
}

export type LvPhase = 'R' | 'W' | 'B' | 'RWB';

/** One building's service connection (plan 2.2). */
export interface LvConnection {
  loadPointId: string;
  buildingId: string;
  /** Erf number, when the building is on a stand. */
  label: string | null;
  kind: 'residential' | 'special';
  kva: number;
  branch: string;
  node: string | null;
  offsetM: number;
  serviceM: number;
  /** Service distribution box on a pole, e.g. P3-1; null for three-phase loads. */
  box: string | null;
  feeder: string | null;
  distanceM: number | null;
  phase: LvPhase | null;
  /** From the building to where the service meets the network. */
  service: Position[];
}

export interface LvBox {
  id: string;
  pole: string;
  feeder: string | null;
  phase: 'R' | 'W' | 'B' | null;
  loads: number;
  kva: number;
  distanceM: number | null;
}

export interface LvPhaseLoad { customers: number; kva: number; boxes: number }

export interface LvFeederPhases {
  feeder: string;
  phases: Record<'R' | 'W' | 'B', LvPhaseLoad>;
  threePhase: number;
  /** Largest difference between a phase's kVA and the mean of the three, as a percentage of the mean. */
  unbalancePct: number;
}

export interface LvLoadSummary {
  loads: number;
  allocated: number;
  unallocated: number;
  unestimated: number;
  onFeeders: number;
  threePhase: number;
  boxes: number;
  allocatedKva: number;
  longestServiceM: number;
}

export interface LvLoads {
  clause: string;
  summary: LvLoadSummary;
  feeders: LvFeederPhases[];
  boxes: LvBox[];
  connections: LvConnection[];
}

export interface LvNetwork {
  id: string;
  rulesRef: string;
  rulesHash: string;
  /** Where the joining tolerances come from. */
  clause: string;
  builtAt: string;
  /** Why the network no longer matches the marked routes and sites, or null. */
  stale: string | null;
  summary: LvSummary;
  feeders: LvFeeder[];
  issues: LvIssue[];
  nodes: LvNode[];
  branches: LvBranch[];
  /** How loads are connected and phased; null when the rules file has no service settings. */
  loads: LvLoads | null;
}

/** Feeder colours, in feeder order; branches nothing feeds are grey. */
export const FEEDER_COLOURS = ['#1f6feb', '#bf3989', '#1a7f37', '#9a6700', '#8250df', '#0969da', '#cf222e', '#116329'];
export const UNFED_COLOUR = '#8c959f';

/** Red, white and blue phases; white is drawn dark grey so it shows on a light map. Three-phase is purple. */
export const PHASE_COLOURS: Record<LvPhase, string> = { R: '#cf222e', W: '#57606a', B: '#0969da', RWB: '#8250df' };
export const PHASE_NAMES: Record<'R' | 'W' | 'B', string> = { R: 'Red', W: 'White', B: 'Blue' };

export interface LvBranchProps { kind: LvBranch['kind']; feeder: string | null; colour: string; lengthM: number }
export interface LvNodeProps { kind: LvNodeKind; label: string | null; feeder: string | null }
export interface LvIssueProps { severity: LvIssue['severity']; code: string; message: string }
export interface LvServiceProps { phase: LvPhase | null; colour: string; box: string | null; label: string | null }

/** The network as map layers. */
export interface LvLayers {
  branches: FeatureCollection<LvBranchProps, GeoJsonLineString>;
  nodes: FeatureCollection<LvNodeProps, GeoJsonPoint>;
  issues: FeatureCollection<LvIssueProps, GeoJsonPoint>;
  /** Service connections, coloured by phase, and a dot at each building. */
  services: FeatureCollection<LvServiceProps, GeoJsonLineString>;
  loads: FeatureCollection<LvServiceProps, GeoJsonPoint>;
}

export function lvLayers(net: LvNetwork): LvLayers {
  const order = new Map(net.feeders.map((f, i) => [f.id, i]));
  const connections = net.loads?.connections ?? [];
  const colour = (feeder: string | null) => (feeder !== null && order.has(feeder) ? FEEDER_COLOURS[order.get(feeder)! % FEEDER_COLOURS.length] : UNFED_COLOUR);
  return {
    branches: {
      type: 'FeatureCollection',
      features: net.branches.map((b) => ({
        type: 'Feature', id: b.id, geometry: { type: 'LineString', coordinates: b.coordinates },
        properties: { kind: b.kind, feeder: b.feeder, colour: colour(b.feeder), lengthM: b.lengthM },
      })),
    },
    nodes: {
      type: 'FeatureCollection',
      features: net.nodes.map((n) => ({
        type: 'Feature', id: n.id, geometry: { type: 'Point', coordinates: n.coordinates },
        properties: { kind: n.kind, label: n.label, feeder: n.feeder },
      })),
    },
    issues: {
      type: 'FeatureCollection',
      features: net.issues.flatMap((i) => i.at.map((at, k) => ({
        type: 'Feature' as const, id: `${i.code}-${k}`, geometry: { type: 'Point' as const, coordinates: at },
        properties: { severity: i.severity, code: i.code, message: i.message },
      }))),
    },
    services: {
      type: 'FeatureCollection',
      features: connections.map((c) => ({
        type: 'Feature', id: c.loadPointId, geometry: { type: 'LineString', coordinates: c.service }, properties: serviceProps(c),
      })),
    },
    loads: {
      type: 'FeatureCollection',
      features: connections.map((c) => ({
        type: 'Feature', id: c.loadPointId, geometry: { type: 'Point', coordinates: c.service[0] }, properties: serviceProps(c),
      })),
    },
  };
}

function serviceProps(c: LvConnection): LvServiceProps {
  return { phase: c.phase, colour: c.phase ? PHASE_COLOURS[c.phase] : UNFED_COLOUR, box: c.box, label: c.label };
}

/** A conductor or cable from the project's rules file (plan 2.3). */
export interface Conductor {
  code: string;
  description: string;
  kind: 'overhead' | 'underground';
  material: 'cu' | 'al' | null;
  sizeMm2: number | null;
  cores: number | null;
  uses: ('feeder' | 'service')[];
  rOhmPerKm: number;
  xOhmPerKm: number;
  ratingA: number;
  /** Rating by installation: ground, pipe, air. */
  ratingsA: Partial<Record<'ground' | 'pipe' | 'air', number>>;
  faultK: number | null;
  /** Short-circuit withstand for 1 s. */
  oneSecondKa: number | null;
  /** Fields whose values are placeholders, not yet from the governing standard. */
  placeholder: string[];
  clause: string;
  ratingClause: string;
  index: string;
}

export interface ConductorLibrary {
  rulesRef: string;
  rulesHash: string;
  conductors: Conductor[];
}

@Injectable({ providedIn: 'root' })
export class LvNetworkApi {
  private readonly http = inject(HttpClient);

  get(projectId: string): Observable<LvNetwork | null> {
    return this.http.get<{ network: LvNetwork | null }>(`/api/projects/${projectId}/lv-network`).pipe(map((r) => r.network));
  }

  conductors(projectId: string): Observable<ConductorLibrary> {
    return this.http.get<ConductorLibrary>(`/api/projects/${projectId}/conductors`);
  }

  /** Builds the network again from the LV routes and sites marked now. */
  build(projectId: string): Observable<LvNetwork> {
    return this.http.post<LvNetwork>(`/api/projects/${projectId}/lv-network`, null);
  }
}
