import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { GeoJsonPolygon } from './geo';

export interface Feature<P> {
  type: 'Feature';
  id: string;
  geometry: GeoJsonPolygon;
  properties: P;
}

export interface FeatureCollection<P> {
  type: 'FeatureCollection';
  features: Feature<P>[];
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

export interface BuildingProps {
  predictedType: string;
  confidence: number;
  source: string;
  lowConfidence: boolean;
  status: 'predicted' | 'confirmed' | 'notpresent' | 'new';
  confirmedType: string | null;
  areaM2: number;
  erf: string | null;
  zoning: string | null;
  signals: PredictionSignal[];
}

export interface PreviewProps {
  ref: string;
  erf: string | null;
  areaM2: number;
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
}

export type ImportKind = 'stands' | 'buildings';

export interface ImportOptions {
  kind: ImportKind;
  file: File;
  sourceCrs?: string;
  layer?: string;
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

  buildings(projectId: string): Observable<FeatureCollection<BuildingProps>> {
    return this.http.get<FeatureCollection<BuildingProps>>(`${this.base(projectId)}/buildings`);
  }

  import(projectId: string, o: ImportOptions): Observable<ImportResponse> {
    const form = new FormData();
    form.append('file', o.file, o.file.name);
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
