import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { GeoJsonLineString, GeoJsonPoint, GeoJsonPolygon } from './geo';

export type { GeoJsonLineString, GeoJsonPoint };

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
  name?: string | null;
  category?: string | null;
  elevationM?: number | null;
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
  lines?: number;
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
  preview: FeatureCollection<PreviewProps> | null;
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
  /** Assets lacking fields their type needs. */
  networkIncomplete: number;
}

export type ImportKind = 'stands' | 'buildings' | 'roads' | 'contours' | 'network';
/** Kinds that can be fetched from OpenStreetMap instead of a file. */
export const OSM_KINDS: ImportKind[] = ['buildings', 'roads'];

export interface ImportOptions {
  kind: ImportKind;
  /** A file to read, or none to fetch from OpenStreetMap. */
  file?: File;
  source?: 'osm';
  sourceCrs?: string;
  layer?: string;
  dryRun: boolean;
}

export interface RoadProps {
  name: string | null;
  roadClass: string | null;
  lengthM: number;
  osmId: string | null;
}

export interface ContourProps {
  elevationM: number;
}

export type NetworkAssetType =
  | 'connection_point' | 'substation' | 'minisub' | 'transformer' | 'switchgear' | 'pole' | 'other'
  | 'mv_line' | 'lv_line' | 'mv_cable' | 'lv_cable' | 'other_line';

export interface NetworkProps {
  assetType: NetworkAssetType;
  label: string | null;
  voltageKv: number | null;
  ratingKva: number | null;
  capacityKva: number | null;
  faultLevelKa: number | null;
  /** Fields the asset's type needs that the authority's data did not give. */
  missing: string[];
}

export type Network = FeatureCollection<NetworkProps, GeoJsonPoint | GeoJsonLineString>;

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

  roads(projectId: string): Observable<FeatureCollection<RoadProps, GeoJsonLineString>> {
    return this.http.get<FeatureCollection<RoadProps, GeoJsonLineString>>(`${this.base(projectId)}/roads`);
  }

  contours(projectId: string): Observable<FeatureCollection<ContourProps, GeoJsonLineString>> {
    return this.http.get<FeatureCollection<ContourProps, GeoJsonLineString>>(`${this.base(projectId)}/contours`);
  }

  network(projectId: string): Observable<Network> {
    return this.http.get<Network>(`${this.base(projectId)}/network`);
  }

  import(projectId: string, o: ImportOptions): Observable<ImportResponse> {
    const form = new FormData();
    if (o.file) form.append('file', o.file, o.file.name);
    if (o.source) form.append('source', o.source);
    form.append('kind', o.kind);
    form.append('dryRun', String(o.dryRun));
    if (o.sourceCrs) form.append('sourceCrs', o.sourceCrs);
    if (o.layer) form.append('layer', o.layer);
    return this.http.post<ImportResponse>(`${this.base(projectId)}/imports`, form);
  }

  repredict(projectId: string): Observable<void> {
    return this.http.post<void>(`${this.base(projectId)}/predictions`, null);
  }

  private base(projectId: string): string {
    return `/api/projects/${encodeURIComponent(projectId)}`;
  }
}

/** Map styles cannot read array properties, so mark assets that lack fields with a flag. */
export function flagIncomplete<T extends FeatureCollection<unknown, AnyGeometry> | null>(c: T): T {
  if (!c) return c;
  return {
    ...c,
    features: c.features.map((f) => {
      const missing = (f.properties as { missing?: unknown[] } | null)?.missing;
      return { ...f, properties: { ...(f.properties as object), incomplete: Array.isArray(missing) && missing.length > 0 } };
    }),
  } as T;
}
