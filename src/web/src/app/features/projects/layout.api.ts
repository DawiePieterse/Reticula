import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { GeoJsonPolygon, Position } from './geo';

export interface GeoJsonPoint {
  type: 'Point';
  coordinates: Position;
}

export interface GeoJsonLineString {
  type: 'LineString';
  coordinates: Position[];
}

export type AnyGeometry = GeoJsonPolygon | GeoJsonPoint | GeoJsonLineString;

export interface Feature<P, G extends AnyGeometry = GeoJsonPolygon> {
  type: 'Feature';
  id: string;
  geometry: G;
  properties: P;
}

export interface FeatureCollection<P, G extends AnyGeometry = GeoJsonPolygon> {
  type: 'FeatureCollection';
  features: Feature<P, G>[];
}

export interface StandProps {
  erf: string | null;
  zoning: string | null;
  areaM2: number;
}

export interface PredictionSignal {
  source: string;
  type: string;
  confidence: number;
}

export type BuildingStatus = 'predicted' | 'confirmed' | 'notpresent' | 'new';

export interface BuildingProps {
  predictedType: string;
  confidence: number;
  source: string;
  lowConfidence: boolean;
  status: BuildingStatus;
  confirmedType: string | null;
  effectiveType: string;
  areaM2: number;
  erf: string | null;
  zoning: string | null;
  signals: PredictionSignal[];
  version: number;
}

export interface PreviewProps {
  ref: string;
  erf: string | null;
  areaM2: number;
  lengthM?: number;
  name?: string | null;
  subtype?: string | null;
  elevationM?: number | null;
}

export type MapLayer = 'roads' | 'contours' | 'network';

/** A road, contour line or existing network asset. */
export interface MapFeatureProps {
  layer: MapLayer;
  subtype: string | null;
  name: string | null;
  elevationM: number | null;
  lengthM: number;
  attributes: Record<string, unknown>;
}

export interface ImportIssue {
  severity: 'error' | 'warning';
  code: string;
  message: string;
  count: number;
  samples: string[];
}

export interface DxfLayer {
  name: string;
  closedPolylines: number;
  openPolylines: number;
  texts: number;
  points?: number;
}

export interface ImportResponse {
  batchId: string | null;
  committed: boolean;
  format: string;
  sourceCrs: string | null;
  crsReason: string;
  featureCount: number;
  issues: ImportIssue[];
  layers: DxfLayer[];
  preview: FeatureCollection<PreviewProps, AnyGeometry> | null;
}

export interface LayoutSummary {
  stands: number;
  standsWithoutErf: number;
  buildings: number;
  lowConfidence: number;
  inspected: number;
  predictedByType: Record<string, number>;
  roads: number;
  contours: number;
  networkAssets: number;
}

export type ImportKind = 'stands' | 'buildings' | MapLayer;

export interface ImportOptions {
  kind: ImportKind;
  file: File;
  sourceCrs?: string;
  layer?: string;
  contourInterval?: number | null;
  dryRun: boolean;
}

export const BUILDING_COLOURS: Record<string, string> = {
  house: '#1f6feb',
  shop: '#bf8700',
  school: '#8250df',
  other: '#6e7781',
};

@Injectable({ providedIn: 'root' })
export class LayoutApi {
  private readonly http = inject(HttpClient);

  summary(projectId: string): Observable<LayoutSummary> {
    return this.http.get<LayoutSummary>(`${this.base(projectId)}/layout-summary`);
  }

  stands(projectId: string): Observable<FeatureCollection<StandProps>> {
    return this.http.get<FeatureCollection<StandProps>>(`${this.base(projectId)}/stands`);
  }

  buildings(projectId: string): Observable<FeatureCollection<BuildingProps, GeoJsonPolygon | GeoJsonPoint>> {
    return this.http.get<FeatureCollection<BuildingProps, GeoJsonPolygon | GeoJsonPoint>>(`${this.base(projectId)}/buildings`);
  }

  import(projectId: string, o: ImportOptions): Observable<ImportResponse> {
    const form = new FormData();
    form.append('file', o.file, o.file.name);
    form.append('kind', o.kind);
    form.append('dryRun', String(o.dryRun));
    if (o.sourceCrs) form.append('sourceCrs', o.sourceCrs);
    if (o.layer) form.append('layer', o.layer);
    if (o.contourInterval) form.append('contourInterval', String(o.contourInterval));
    return this.http.post<ImportResponse>(`${this.base(projectId)}/imports`, form);
  }

  /** Fetches buildings or roads for the project area from OpenStreetMap, checked like a file. */
  importOverpass(projectId: string, kind: 'buildings' | 'roads', dryRun: boolean): Observable<ImportResponse> {
    return this.http.post<ImportResponse>(`${this.base(projectId)}/imports/overpass`, { kind, dryRun });
  }

  mapFeatures(projectId: string, layer?: MapLayer): Observable<FeatureCollection<MapFeatureProps, AnyGeometry>> {
    return this.http.get<FeatureCollection<MapFeatureProps, AnyGeometry>>(`${this.base(projectId)}/map-features${layer ? `?layer=${layer}` : ''}`);
  }

  repredict(projectId: string): Observable<void> {
    return this.http.post<void>(`${this.base(projectId)}/predictions`, null);
  }

  private base(projectId: string): string {
    return `/api/projects/${encodeURIComponent(projectId)}`;
  }
}
