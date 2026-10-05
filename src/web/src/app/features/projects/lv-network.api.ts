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
}

/** Feeder colours, in feeder order; branches nothing feeds are grey. */
export const FEEDER_COLOURS = ['#1f6feb', '#bf3989', '#1a7f37', '#9a6700', '#8250df', '#0969da', '#cf222e', '#116329'];
export const UNFED_COLOUR = '#8c959f';

export interface LvBranchProps { kind: LvBranch['kind']; feeder: string | null; colour: string; lengthM: number }
export interface LvNodeProps { kind: LvNodeKind; label: string | null; feeder: string | null }
export interface LvIssueProps { severity: LvIssue['severity']; code: string; message: string }

/** The network as map layers. */
export interface LvLayers {
  branches: FeatureCollection<LvBranchProps, GeoJsonLineString>;
  nodes: FeatureCollection<LvNodeProps, GeoJsonPoint>;
  issues: FeatureCollection<LvIssueProps, GeoJsonPoint>;
}

export function lvLayers(net: LvNetwork): LvLayers {
  const order = new Map(net.feeders.map((f, i) => [f.id, i]));
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
  };
}

@Injectable({ providedIn: 'root' })
export class LvNetworkApi {
  private readonly http = inject(HttpClient);

  get(projectId: string): Observable<LvNetwork | null> {
    return this.http.get<{ network: LvNetwork | null }>(`/api/projects/${projectId}/lv-network`).pipe(map((r) => r.network));
  }

  /** Builds the network again from the LV routes and sites marked now. */
  build(projectId: string): Observable<LvNetwork> {
    return this.http.post<LvNetwork>(`/api/projects/${projectId}/lv-network`, null);
  }
}
